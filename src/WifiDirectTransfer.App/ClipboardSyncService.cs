using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

internal enum ClipboardReadResult
{
    Success,
    Empty,
    Error
}

internal sealed class ClipboardSyncService : IDisposable
{
    public const int WmClipboardUpdate = 0x031D;

    private const int ClipboardRetryCount = 3;
    private const int ClipboardRetryDelayMilliseconds = 50;
    private const int RemoteEchoSuppressMilliseconds = 2000;

    // 复制大文件或从图片查看器复制时，剪贴板可能几秒内都无法读取。
    private static readonly int[] ReadRetryDelaysMilliseconds =
    {
        100,
        300,
        800,
        2000
    };

    private static readonly HashSet<string> ImageFileExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".jpe",
            ".jfif",
            ".bmp",
            ".dib",
            ".gif",
            ".tif",
            ".tiff",
            ".ico",
            ".wdp",
            ".jxr"
        };

    private readonly record struct ClipboardSnapshot(
        ClipboardContentType Type,
        byte[]? TextBytes,
        string? ImageFilePath,
        BitmapSource? ClipboardImage);

    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _log;
    private readonly Action<string>? _status;
    private readonly CancellationToken _lifetimeToken;

    private IntPtr _windowHandle;
    private bool _listenerRegistered;
    private bool _disposed;
    private volatile bool _enabled;
    private bool _isApplyingRemoteClipboard;
    private DateTimeOffset _ignoreClipboardUpdateUntil;
    private Func<ReadOnlyMemory<byte>, CancellationToken, Task>? _sendAsync;
    private CancellationTokenSource? _sendCts;
    private byte[]? _lastLocalHash;
    private byte[]? _lastRemoteHash;
    private byte[]? _lastSentHash;
    private int _clipboardReadGeneration;
    private bool _notConnectedLogged;
    private bool _disabledDropLogged;

    public ClipboardSyncService(
        Dispatcher dispatcher,
        Action<string> log,
        Action<string>? status,
        CancellationToken lifetimeToken)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(log);

        _dispatcher = dispatcher;
        _log = log;
        _status = status;
        _lifetimeToken = lifetimeToken;
    }

    public void Attach(IntPtr windowHandle)
    {
        if (_disposed)
        {
            return;
        }

        _windowHandle = windowHandle;

        if (_enabled)
        {
            RegisterListener();
        }
    }

    public void Detach()
    {
        UnregisterListener();
        _windowHandle = IntPtr.Zero;
    }

    public void SetEnabled(bool enabled)
    {
        if (_disposed || _enabled == enabled)
        {
            return;
        }

        _enabled = enabled;

        if (enabled)
        {
            _sendCts = CreateSendCancellation();
            _clipboardReadGeneration++;
            RegisterListener();
        }
        else
        {
            _clipboardReadGeneration++;
            CancelSends();
            UnregisterListener();
            ClearHashes();
            _ignoreClipboardUpdateUntil = default;
            _notConnectedLogged = false;
            _disabledDropLogged = false;
        }
    }

    public void SetSender(
        Func<ReadOnlyMemory<byte>, CancellationToken, Task>? sendAsync)
    {
        _sendAsync = sendAsync;

        if (sendAsync is null)
        {
            CancelSends();
        }
        else if (_enabled && _sendCts is null)
        {
            _sendCts = CreateSendCancellation();
        }
    }

    public void OnClipboardUpdate()
    {
        if (!_enabled || _disposed)
        {
            return;
        }

        if (_isApplyingRemoteClipboard)
        {
            return;
        }

        if (_ignoreClipboardUpdateUntil > DateTimeOffset.UtcNow)
        {
            // 整段时间窗内都忽略，避免剪贴板管理器产生多条回显消息。
            return;
        }

        var sendAsync = _sendAsync;

        if (sendAsync is null)
        {
            if (!_notConnectedLogged)
            {
                _notConnectedLogged = true;
                _log("剪贴板更新：尚未建立连接，已跳过。");
            }

            return;
        }

        _notConnectedLogged = false;

        var generation = ++_clipboardReadGeneration;
        _log("剪贴板更新：开始读取。");

        TryProcessClipboardUpdate(generation, attempt: 0, sendAsync);
    }

    private void TryProcessClipboardUpdate(
        int generation,
        int attempt,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync)
    {
        if (_disposed ||
            !_enabled ||
            generation != _clipboardReadGeneration ||
            !ReferenceEquals(_sendAsync, sendAsync))
        {
            return;
        }

        var result = TryReadClipboard(out var snapshot, out var description);

        if (result == ClipboardReadResult.Success && snapshot is not null)
        {
            if (attempt > 0)
            {
                _log($"剪贴板读取在重试 {attempt} 次后成功（{description}）。");
            }

            _ = ProcessSnapshotAsync(
                generation,
                snapshot.Value,
                description,
                sendAsync);
            return;
        }

        if (result == ClipboardReadResult.Empty &&
            attempt < ReadRetryDelaysMilliseconds.Length)
        {
            _ = RetryClipboardUpdateAsync(
                generation,
                attempt + 1,
                sendAsync,
                ReadRetryDelaysMilliseconds[attempt]);
            return;
        }

        if (result == ClipboardReadResult.Empty)
        {
            _log(
                "剪贴板中没有可同步的内容，已放弃本次更新" +
                $"（尝试 {attempt + 1} 次；可能剪贴板被占用或数据尚未就绪）。");
        }
    }

    private async Task ProcessSnapshotAsync(
        int generation,
        ClipboardSnapshot snapshot,
        string description,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync)
    {
        ClipboardContent? content = null;
        var finalDescription = description;

        try
        {
            switch (snapshot.Type)
            {
                case ClipboardContentType.Text when snapshot.TextBytes is not null:
                    content = new ClipboardContent(
                        ClipboardContentType.Text,
                        snapshot.TextBytes);
                    break;

                case ClipboardContentType.Image
                    when snapshot.ImageFilePath is not null:
                    (content, finalDescription) = await Task.Run(
                            () => LoadImageFile(snapshot.ImageFilePath),
                            _lifetimeToken)
                        .ConfigureAwait(false);
                    break;

                case ClipboardContentType.Image
                    when snapshot.ClipboardImage is not null:
                    var image = snapshot.ClipboardImage;
                    var pngBytes = await Task.Run(
                            () => EncodePng(image),
                            _lifetimeToken)
                        .ConfigureAwait(false);
                    content = new ClipboardContent(
                        ClipboardContentType.Image,
                        pngBytes);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            _log($"处理剪贴板内容失败：{exception.GetType().Name}");
            return;
        }

        if (content is null)
        {
            return;
        }

        try
        {
            await _dispatcher
                .InvokeAsync(
                    () =>
                    {
                        if (_disposed ||
                            !_enabled ||
                            generation != _clipboardReadGeneration ||
                            !ReferenceEquals(_sendAsync, sendAsync))
                        {
                            return;
                        }

                        ProcessClipboardContent(
                            sendAsync,
                            content,
                            finalDescription);
                    },
                    DispatcherPriority.Normal,
                    _lifetimeToken)
                .Task
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭或同步关闭。
        }
    }

    private async Task RetryClipboardUpdateAsync(
        int generation,
        int attempt,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync,
        int delayMilliseconds)
    {
        try
        {
            await Task.Delay(delayMilliseconds, _lifetimeToken)
                .ConfigureAwait(false);

            await _dispatcher
                .InvokeAsync(
                    () => TryProcessClipboardUpdate(
                        generation,
                        attempt,
                        sendAsync),
                    DispatcherPriority.Normal,
                    _lifetimeToken)
                .Task
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭或同步关闭。
        }
    }

    private void ProcessClipboardContent(
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync,
        ClipboardContent content,
        string description)
    {
        if (content.Type == ClipboardContentType.Image &&
            content.Data.Length > ClipboardMessageCodec.MaxImageBytes)
        {
            _log(
                "剪贴板图片过大，已跳过同步" +
                $"（PNG {content.Data.Length} 字节，{description}）");
            return;
        }

        if (content.Type == ClipboardContentType.Text &&
            content.Data.Length > ClipboardMessageCodec.MaxPayloadLength - 1)
        {
            _log("剪贴板文本过大，已跳过同步");
            return;
        }

        var hash = SHA256.HashData(content.Data);

        if (HashesEqual(hash, _lastRemoteHash))
        {
            _lastLocalHash = hash;
            _log("剪贴板内容与远端回写一致，已忽略。");
            return;
        }

        if (HashesEqual(hash, _lastSentHash))
        {
            _log("剪贴板内容未变化，已忽略。");
            return;
        }

        _lastLocalHash = hash;

        byte[] payload;

        try
        {
            payload = ClipboardMessageCodec.Encode(content.Type, content.Data);
        }
        catch (Exception exception)
        {
            _log($"剪贴板内容无法同步：{exception.Message}");
            return;
        }

        var token = _sendCts?.Token ?? CancellationToken.None;
        _ = SendPayloadAsync(
            sendAsync,
            content,
            description,
            payload,
            hash,
            token);
    }

    public async Task HandleRemotePayloadAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled || _disposed)
        {
            if (!_disabledDropLogged)
            {
                _disabledDropLogged = true;
                _log("收到远端剪贴板数据，但本机剪贴板同步未开启，已忽略。");
            }

            return;
        }

        _disabledDropLogged = false;

        ClipboardContent content;

        try
        {
            content = ClipboardMessageCodec.Deserialize(payload.Span);
        }
        catch (Exception exception)
        {
            _log($"剪贴板同步数据无效：{exception.Message}");
            return;
        }

        _log($"收到远端剪贴板：{DescribeContent(content)}。");

        if (content.Type == ClipboardContentType.Image &&
            content.Data.Length > ClipboardMessageCodec.MaxImageBytes)
        {
            _log(
                "剪贴板图片过大，已跳过同步" +
                $"（PNG {content.Data.Length} 字节）");
            return;
        }

        var hash = SHA256.HashData(content.Data);

        BitmapSource? image = null;

        if (content.Type == ClipboardContentType.Image)
        {
            try
            {
                var imageBytes = content.Data;

                // 解码放在后台线程，避免 75 MP 图片阻塞消息循环。
                image = await Task.Run(
                        () => DecodePng(imageBytes),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                _log($"解码剪贴板图片失败：{exception.GetType().Name}");
                return;
            }
        }

        try
        {
            await _dispatcher
                .InvokeAsync(
                    () => ApplyRemoteContent(content, image, hash),
                    DispatcherPriority.Normal,
                    cancellationToken)
                .Task
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭或操作被取消。
        }
        catch (Exception exception)
        {
            _log($"写入本机剪贴板失败：{exception.GetType().Name}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _clipboardReadGeneration++;
        CancelSends();
        UnregisterListener();
        _windowHandle = IntPtr.Zero;
        _sendAsync = null;
        ClearHashes();
    }

    private void ApplyRemoteContent(
        ClipboardContent content,
        BitmapSource? image,
        byte[] hash)
    {
        if (!_enabled || _disposed)
        {
            return;
        }

        // 先记录远端哈希，再写入，避免 WM_CLIPBOARDUPDATE 回环发送。
        _lastRemoteHash = hash;
        _isApplyingRemoteClipboard = true;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!TryWriteRemoteContent(content, image, out var description))
            {
                _lastRemoteHash = null;
                return;
            }

            stopwatch.Stop();
            _log(
                $"已写入本机剪贴板：{description}，" +
                $"耗时 {stopwatch.ElapsedMilliseconds} ms。");
            _status?.Invoke($"已同步远端剪贴板：{description}");

            if (content.Type == ClipboardContentType.Image)
            {
                // 图片写入后不再完整回读重编码（75 MP 要 2 秒以上）；
                // 自己的回显由时间窗抑制。
                _lastLocalHash = hash;
                _ignoreClipboardUpdateUntil =
                    DateTimeOffset.UtcNow.AddMilliseconds(
                        RemoteEchoSuppressMilliseconds);
            }
            else if (TryReadClipboard(out var localSnapshot, out _) ==
                         ClipboardReadResult.Success &&
                     localSnapshot is
                     {
                         Type: ClipboardContentType.Text,
                         TextBytes: { } localBytes
                     })
            {
                var localHash = SHA256.HashData(localBytes);
                _lastRemoteHash = localHash;
                _lastLocalHash = localHash;
            }
            else
            {
                // 读取回显失败时忽略下一次更新作为兜底。
                _lastLocalHash = hash;
                _ignoreClipboardUpdateUntil =
                    DateTimeOffset.UtcNow.AddMilliseconds(
                        RemoteEchoSuppressMilliseconds);
            }
        }
        catch (Exception exception)
        {
            _lastRemoteHash = null;
            _log(
                $"写入本机剪贴板失败：{exception.GetType().Name}，" +
                exception.Message);
        }
        finally
        {
            _isApplyingRemoteClipboard = false;
        }
    }

    private bool TryWriteRemoteContent(
        ClipboardContent content,
        BitmapSource? decodedImage,
        out string description)
    {
        BitmapSource? image = decodedImage;
        description = string.Empty;

        for (var attempt = 0; attempt <= ClipboardRetryCount; attempt++)
        {
            try
            {
                switch (content.Type)
                {
                    case ClipboardContentType.Text:
                        Clipboard.SetText(
                            ClipboardMessageCodec.DecodeText(content.Data));
                        description = $"文本 {FormatBytes(content.Data.Length)}";
                        return true;

                    case ClipboardContentType.Image:
                        image ??= DecodePng(content.Data);
                        Clipboard.SetImage(image);
                        description =
                            $"图片 {image.PixelWidth}x{image.PixelHeight}" +
                            $"（{FormatBytes(content.Data.Length)}）";
                        return true;

                    default:
                        return false;
                }
            }
            catch (COMException) when (attempt < ClipboardRetryCount)
            {
                Thread.Sleep(ClipboardRetryDelayMilliseconds);
            }
            catch (COMException)
            {
                _log("写入本机剪贴板失败：剪贴板被占用。");
                return false;
            }
        }

        return false;
    }

    private ClipboardReadResult TryReadClipboard(
        out ClipboardSnapshot? snapshot,
        out string description)
    {
        snapshot = null;
        description = string.Empty;

        for (var attempt = 0; attempt <= ClipboardRetryCount; attempt++)
        {
            try
            {
                return ReadClipboardOnce(out snapshot, out description);
            }
            catch (COMException) when (attempt < ClipboardRetryCount)
            {
                // 剪贴板可能仍被复制源占用，短暂等待后重试。
                Thread.Sleep(ClipboardRetryDelayMilliseconds);
            }
            catch (COMException)
            {
                // 交给上层延迟重试；最终仍失败时统一打印一次。
                return ClipboardReadResult.Empty;
            }
            catch (Exception exception)
            {
                _log($"读取剪贴板失败：{exception.GetType().Name}");
                return ClipboardReadResult.Error;
            }
        }

        return ClipboardReadResult.Empty;
    }

    private ClipboardReadResult ReadClipboardOnce(
        out ClipboardSnapshot? snapshot,
        out string description)
    {
        snapshot = null;
        description = string.Empty;

        if (Clipboard.ContainsFileDropList())
        {
            var fileDropList = Clipboard.GetFileDropList();

            if (fileDropList is null || fileDropList.Count == 0)
            {
                return ClipboardReadResult.Empty;
            }

            var imagePaths = new List<string>();

            foreach (string? path in fileDropList)
            {
                if (!string.IsNullOrWhiteSpace(path) &&
                    ImageFileExtensions.Contains(Path.GetExtension(path)))
                {
                    imagePaths.Add(path);
                }
            }

            if (imagePaths.Count == 0)
            {
                _log("剪贴板文件不是支持的图片，已跳过同步。");
                return ClipboardReadResult.Error;
            }

            if (imagePaths.Count > 1)
            {
                _log(
                    $"剪贴板包含 {imagePaths.Count} 个图片文件，" +
                    "仅同步第一个。");
            }

            var imagePath = imagePaths[0];
            snapshot = new ClipboardSnapshot(
                ClipboardContentType.Image,
                null,
                imagePath,
                null);
            description = $"文件 {Path.GetFileName(imagePath)}";
            return ClipboardReadResult.Success;
        }

        if (Clipboard.ContainsImage())
        {
            var image = Clipboard.GetImage();

            if (image is not null)
            {
                // 冻结后才能在后台线程编码。
                if (image.CanFreeze)
                {
                    image.Freeze();
                }
                else
                {
                    var frozenCopy = new WriteableBitmap(image);
                    frozenCopy.Freeze();
                    image = frozenCopy;
                }

                snapshot = new ClipboardSnapshot(
                    ClipboardContentType.Image,
                    null,
                    null,
                    image);
                description =
                    $"剪贴板位图 {image.PixelWidth}x{image.PixelHeight}";
                return ClipboardReadResult.Success;
            }

            return ClipboardReadResult.Empty;
        }

        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText();
            var bytes = Encoding.UTF8.GetBytes(text);

            snapshot = new ClipboardSnapshot(
                ClipboardContentType.Text,
                bytes,
                null,
                null);
            description = $"文本 {FormatBytes(bytes.Length)}";
            return ClipboardReadResult.Success;
        }

        return ClipboardReadResult.Empty;
    }

    private (ClipboardContent? Content, string Description) LoadImageFile(
        string path)
    {
        try
        {
            var fileName = Path.GetFileName(path);

            if (string.Equals(
                    Path.GetExtension(path),
                    ".png",
                    StringComparison.OrdinalIgnoreCase))
            {
                var pngBytes = File.ReadAllBytes(path);

                if (pngBytes.Length > ClipboardMessageCodec.MaxImageBytes)
                {
                    _log(
                        "剪贴板图片过大，已跳过同步" +
                        $"（PNG {pngBytes.Length} 字节，文件 {fileName}）");
                    return (null, string.Empty);
                }

                // 只校验 PNG 头，避免为验证而完整解码 75 MP 图片。
                if (!HasValidPngHeader(pngBytes))
                {
                    _log($"剪贴板图片文件不是有效的 PNG：{fileName}");
                    return (null, string.Empty);
                }

                return (
                    new ClipboardContent(
                        ClipboardContentType.Image,
                        pngBytes),
                    $"文件 {fileName}（原始 PNG）");
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
            {
                _log($"剪贴板图片文件没有可用帧：{fileName}");
                return (null, string.Empty);
            }

            var frame = decoder.Frames[0];
            var description =
                $"文件 {fileName}（解码为 PNG，" +
                $"{frame.PixelWidth}x{frame.PixelHeight}）";

            return (
                new ClipboardContent(
                    ClipboardContentType.Image,
                    EncodePng(frame)),
                description);
        }
        catch (Exception exception)
        {
            _log(
                $"读取剪贴板图片文件失败：{Path.GetFileName(path)}" +
                $"（{exception.GetType().Name}）");
            return (null, string.Empty);
        }
    }

    private static bool HasValidPngHeader(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature =
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        return bytes.Length >= 33 &&
               bytes[..8].SequenceEqual(signature) &&
               bytes[12] == (byte)'I' &&
               bytes[13] == (byte)'H' &&
               bytes[14] == (byte)'D' &&
               bytes[15] == (byte)'R' &&
               BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4)) > 0 &&
               BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4)) > 0;
    }

    private async Task SendPayloadAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync,
        ClipboardContent content,
        string description,
        byte[] payload,
        byte[] hash,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await sendAsync(payload, cancellationToken).ConfigureAwait(false);
            _lastSentHash = hash;
            stopwatch.Stop();
            _log(
                $"剪贴板已发送：{DescribeContent(content)}，" +
                $"payload {payload.Length} 字节，" +
                $"耗时 {stopwatch.ElapsedMilliseconds} ms（{description}）。");
        }
        catch (OperationCanceledException)
        {
            // 同步已关闭或连接已重置。
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _log(
                $"剪贴板同步发送失败：{exception.GetType().Name}，" +
                $"{exception.Message}（{DescribeContent(content)}，" +
                $"耗时 {stopwatch.ElapsedMilliseconds} ms）。");
        }
    }

    private static string DescribeContent(ClipboardContent content) =>
        content.Type == ClipboardContentType.Image
            ? $"图片 {content.Data.Length} 字节"
            : $"文本 {content.Data.Length} 字节";

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} 字节",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.0} MB"
    };

    private CancellationTokenSource CreateSendCancellation() =>
        CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);

    private void CancelSends()
    {
        var sendCts = _sendCts;
        _sendCts = null;
        sendCts?.Cancel();
        sendCts?.Dispose();
    }

    private void ClearHashes()
    {
        _lastLocalHash = null;
        _lastRemoteHash = null;
        _lastSentHash = null;
    }

    private void RegisterListener()
    {
        if (_listenerRegistered ||
            _windowHandle == IntPtr.Zero ||
            _disposed)
        {
            return;
        }

        if (AddClipboardFormatListener(_windowHandle))
        {
            _listenerRegistered = true;
        }
        else
        {
            _log("无法注册剪贴板监听，剪贴板同步不可用。");
        }
    }

    private void UnregisterListener()
    {
        if (!_listenerRegistered)
        {
            return;
        }

        RemoveClipboardFormatListener(_windowHandle);
        _listenerRegistered = false;
    }

    private static bool HashesEqual(byte[]? left, byte[]? right) =>
        left is not null &&
        right is not null &&
        CryptographicOperations.FixedTimeEquals(left, right);

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource DecodePng(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes, writable: false);
        var image = new BitmapImage();

        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();

        return image;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}

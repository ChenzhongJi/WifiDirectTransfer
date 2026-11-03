using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

internal sealed class ClipboardSyncService : IDisposable
{
    public const int WmClipboardUpdate = 0x031D;

    private const int ClipboardRetryCount = 3;
    private const int ClipboardRetryDelayMilliseconds = 50;
    private const int RemoteEchoSuppressMilliseconds = 2000;

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

    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _log;
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

    public ClipboardSyncService(
        Dispatcher dispatcher,
        Action<string> log,
        CancellationToken lifetimeToken)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(log);

        _dispatcher = dispatcher;
        _log = log;
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
            RegisterListener();
        }
        else
        {
            CancelSends();
            UnregisterListener();
            ClearHashes();
            _ignoreClipboardUpdateUntil = default;
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
            _ignoreClipboardUpdateUntil = default;
            return;
        }

        var sendAsync = _sendAsync;

        if (sendAsync is null)
        {
            return;
        }

        if (!TryReadClipboard(out var content) || content is null)
        {
            return;
        }

        if (content.Type == ClipboardContentType.Image &&
            content.Data.Length > ClipboardMessageCodec.MaxImageBytes)
        {
            _log(
                "剪贴板图片过大，已跳过同步" +
                $"（PNG {content.Data.Length} 字节）");
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
        _ = SendPayloadAsync(sendAsync, payload, token);
    }

    public async Task HandleRemotePayloadAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled || _disposed)
        {
            return;
        }

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

        if (content.Type == ClipboardContentType.Image &&
            content.Data.Length > ClipboardMessageCodec.MaxImageBytes)
        {
            _log(
                "剪贴板图片过大，已跳过同步" +
                $"（PNG {content.Data.Length} 字节）");
            return;
        }

        var hash = SHA256.HashData(content.Data);

        try
        {
            await _dispatcher
                .InvokeAsync(
                    () => ApplyRemoteContent(content, hash),
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
        CancelSends();
        UnregisterListener();
        _windowHandle = IntPtr.Zero;
        _sendAsync = null;
        ClearHashes();
    }

    private void ApplyRemoteContent(ClipboardContent content, byte[] hash)
    {
        if (!_enabled || _disposed)
        {
            return;
        }

        // 先记录远端哈希，再写入，避免 WM_CLIPBOARDUPDATE 回环发送。
        _lastRemoteHash = hash;
        _isApplyingRemoteClipboard = true;

        try
        {
            if (!TryWriteRemoteContent(content))
            {
                _lastRemoteHash = null;
                return;
            }

            if (TryReadClipboard(out var localContent) &&
                localContent is not null &&
                localContent.Type == content.Type)
            {
                var localHash = SHA256.HashData(localContent.Data);
                _lastRemoteHash = localHash;
                _lastLocalHash = localHash;
            }
            else
            {
                // 图片重新编码后字节可能变化；读取回显失败时忽略下一次更新作为兜底。
                _lastLocalHash = hash;
                _ignoreClipboardUpdateUntil = DateTimeOffset.UtcNow.AddMilliseconds(
                    RemoteEchoSuppressMilliseconds);
            }
        }
        catch (Exception exception)
        {
            _lastRemoteHash = null;
            _log($"写入本机剪贴板失败：{exception.GetType().Name}");
        }
        finally
        {
            _isApplyingRemoteClipboard = false;
        }
    }

    private bool TryWriteRemoteContent(ClipboardContent content)
    {
        BitmapSource? image = null;

        for (var attempt = 0; attempt <= ClipboardRetryCount; attempt++)
        {
            try
            {
                switch (content.Type)
                {
                    case ClipboardContentType.Text:
                        Clipboard.SetText(
                            ClipboardMessageCodec.DecodeText(content.Data));
                        return true;

                    case ClipboardContentType.Image:
                        image ??= DecodePng(content.Data);
                        Clipboard.SetImage(image);
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

    private bool TryReadClipboard(out ClipboardContent? content)
    {
        content = null;

        for (var attempt = 0; attempt <= ClipboardRetryCount; attempt++)
        {
            try
            {
                content = ReadClipboardOnce();
                return content is not null;
            }
            catch (COMException) when (attempt < ClipboardRetryCount)
            {
                // 剪贴板可能仍被复制源占用，短暂等待后重试。
                Thread.Sleep(ClipboardRetryDelayMilliseconds);
            }
            catch (COMException)
            {
                _log("读取剪贴板失败，已跳过本次同步。");
                return false;
            }
            catch (Exception exception)
            {
                _log($"读取剪贴板失败：{exception.GetType().Name}");
                return false;
            }
        }

        return false;
    }

    private ClipboardContent? ReadClipboardOnce()
    {
        if (Clipboard.ContainsFileDropList())
        {
            var fileDropList = Clipboard.GetFileDropList();

            if (fileDropList is null || fileDropList.Count == 0)
            {
                return null;
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
                return null;
            }

            if (imagePaths.Count > 1)
            {
                _log(
                    $"剪贴板包含 {imagePaths.Count} 个图片文件，" +
                    "仅同步第一个。");
            }

            foreach (var imagePath in imagePaths)
            {
                var content = TryReadImageFile(imagePath);

                if (content is not null)
                {
                    return content;
                }
            }

            return null;
        }

        if (Clipboard.ContainsImage())
        {
            var image = Clipboard.GetImage();

            if (image is not null)
            {
                return new ClipboardContent(
                    ClipboardContentType.Image,
                    EncodePng(image));
            }
        }

        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText();

            return new ClipboardContent(
                ClipboardContentType.Text,
                Encoding.UTF8.GetBytes(text));
        }

        return null;
    }

    private ClipboardContent? TryReadImageFile(string path)
    {
        try
        {
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
                        $"（PNG {pngBytes.Length} 字节）");
                    return null;
                }

                // 校验文件确实是 WPF 可读取的 PNG，然后直接发送原始 PNG 字节，
                // 避免重新编码导致体积膨胀。
                using var validationStream = new MemoryStream(
                    pngBytes,
                    writable: false);
                BitmapDecoder.Create(
                    validationStream,
                    BitmapCreateOptions.None,
                    BitmapCacheOption.OnLoad);

                return new ClipboardContent(
                    ClipboardContentType.Image,
                    pngBytes);
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
                _log($"剪贴板图片文件没有可用帧：{Path.GetFileName(path)}");
                return null;
            }

            return new ClipboardContent(
                ClipboardContentType.Image,
                EncodePng(decoder.Frames[0]));
        }
        catch (Exception exception)
        {
            _log(
                $"读取剪贴板图片文件失败：{Path.GetFileName(path)}" +
                $"（{exception.GetType().Name}）");
            return null;
        }
    }

    private async Task SendPayloadAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await sendAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 同步已关闭或连接已重置。
        }
        catch (Exception exception)
        {
            _log(
                $"剪贴板同步发送失败：{exception.GetType().Name}，" +
                exception.Message);
        }
    }

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

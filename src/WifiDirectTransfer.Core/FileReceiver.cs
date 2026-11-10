using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace WifiDirectTransfer.Core;

/// <summary>
/// M1 文件接收会话：订阅 MessageDispatcher 的 FileStart/FileChunk/FileEnd。
/// 使用 .part + .meta 支持断点续传；每写入并 Flush 一个块后更新 meta，再发送 Ack。
/// </summary>
public sealed class FileReceiver : IAsyncDisposable
{
    private const int MetadataWriteAttempts = 3;
    private const int MetadataRetryDelayMilliseconds = 50;
    private const long MinimumMetadataInterval = 256L * 1024;
    private const long MaximumMetadataInterval = 4L * 1024 * 1024;

    private readonly MessageDispatcher _dispatcher;
    private readonly string _destinationDirectory;
    private readonly FileHeaderCodec _codec;
    private readonly IProgress<FileTransferProgress>? _progress;
    private readonly Func<string, long?> _availableFreeSpaceProvider;
    private readonly Action<string>? _log;

    private SameNamePolicy _sameNamePolicy;
    private long? _pendingQueueBytes;
    private bool _overwriteOnFinalMove;
    private FileStream? _part;
    private IncrementalHash? _hash;
    private FileHeader? _header;
    private string? _rawFileName;
    private string _relativeDirectory = string.Empty;
    private string? _safeFileName;
    private string? _partPath;
    private string? _metaPath;
    private string? _finalPath;
    private long _received;
    private long _expectedSize;
    private long _metadataIntervalBytes;
    private long _lastMetadataAttemptReceived;
    private bool _metadataWarningLogged;
    private Stopwatch? _stopwatch;
    private int _manifestEntryCount;
    private int _disposed;

    public FileReceiver(
        MessageDispatcher dispatcher,
        string destinationDirectory,
        long maxFileSize = FileTransferProtocol.DefaultMaxFileSize,
        IProgress<FileTransferProgress>? progress = null,
        SameNamePolicy sameNamePolicy = SameNamePolicy.AutoRename,
        Func<string, long?>? availableFreeSpaceProvider = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        if (!Enum.IsDefined(sameNamePolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(sameNamePolicy));
        }

        _dispatcher = dispatcher;
        _destinationDirectory = Path.GetFullPath(destinationDirectory);
        _codec = new FileHeaderCodec(maxFileSize);
        _progress = progress;
        _sameNamePolicy = sameNamePolicy;
        _availableFreeSpaceProvider =
            availableFreeSpaceProvider ?? StorageSpace.GetAvailableFreeSpace;
        _log = log;

        _dispatcher.MessageReceived += OnMessageReceivedAsync;
        _dispatcher.Disconnected += OnDisconnected;
    }

    public SameNamePolicy SameNamePolicy
    {
        get => _sameNamePolicy;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _sameNamePolicy = value;
        }
    }

    public event EventHandler<FileStartedEventArgs>? FileStarted;

    public event EventHandler<FileReceivedEventArgs>? FileReceived;

    public event EventHandler<FileFailedEventArgs>? FileFailed;

    public event EventHandler<FileSkippedEventArgs>? FileSkipped;

    public event EventHandler<DirectoryManifestEventArgs>? DirectoriesReceived;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return default;
        }

        _dispatcher.MessageReceived -= OnMessageReceivedAsync;
        _dispatcher.Disconnected -= OnDisconnected;

        // 关闭但保留 .part/.meta，下次连接可以续传。
        PreserveActive(error: null, fileName: null);

        return default;
    }

    private async Task OnMessageReceivedAsync(
        MessageDispatcher sender,
        MessageReceivedEventArgs args,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            switch (args.Type)
            {
                case MessageType.FileStart:
                    await HandleFileStartAsync(args.Payload, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case MessageType.FileChunk:
                    await HandleFileChunkAsync(args.Payload, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case MessageType.FileEnd:
                    await HandleFileEndAsync(args.Payload, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case MessageType.ControlMessage:
                    HandleControlMessage(args.Payload.Span);
                    break;
            }
        }
        catch (Exception exception)
        {
            // 连接/写入类错误保留 .part/.meta，供重连续传。
            PreserveActive(exception, _safeFileName);
        }
    }

    private async Task HandleFileStartAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (_part is not null || _hash is not null || _header is not null)
        {
            PreserveActive(
                new MessageProtocolException("收到新的 FileStart，已暂停上一个文件。"),
                _safeFileName);
        }

        FileStartPayload start;

        try
        {
            start = _codec.DeserializeFileStartPayload(payload.Span);
        }
        catch (Exception exception)
        {
            PreserveActive(exception, fileName: null);
            return;
        }

        string targetDirectory;
        string safeFileName;

        try
        {
            targetDirectory = SafeRelativePath.ResolveDirectory(
                _destinationDirectory,
                start.RelativeDirectory);
            safeFileName = SafeFileName.Sanitize(start.FileName);
        }
        catch (Exception exception)
        {
            await SendTransferErrorBestEffortAsync(exception, cancellationToken)
                .ConfigureAwait(false);
            PreserveActive(exception, fileName: null);
            return;
        }

        var queueBytes = _pendingQueueBytes;
        _pendingQueueBytes = null;

        var targetFileName = safeFileName;
        var skip = false;

        if (PathExists(targetDirectory, safeFileName))
        {
            switch (_sameNamePolicy)
            {
                case SameNamePolicy.AutoRename:
                    targetFileName = GetAvailableFileName(
                        targetDirectory,
                        safeFileName);
                    break;

                case SameNamePolicy.Skip:
                    skip = true;
                    break;

                case SameNamePolicy.Overwrite:
                    break;

                default:
                    skip = true;
                    break;
            }
        }

        var finalPath = Path.Combine(targetDirectory, targetFileName);
        var partPath = finalPath + ".part";
        var metaPath = finalPath + ".meta";

        var requiredBytes = queueBytes.HasValue
            ? Math.Max(queueBytes.Value, start.Header.FileSize)
            : start.Header.FileSize;
        var availableBytes = _availableFreeSpaceProvider(_destinationDirectory);

        if (availableBytes.HasValue && availableBytes.Value < requiredBytes)
        {
            var storageError = new InsufficientStorageException(
                requiredBytes,
                availableBytes.Value);

            try
            {
                await SendTransferErrorAsync(storageError, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 连接已经断开时，仍要把失败原因交给本地 UI。
            }

            PreserveActive(storageError, targetFileName);
            return;
        }

        if (skip)
        {
            await SendFileSkippedAsync(cancellationToken).ConfigureAwait(false);
            RaiseFileSkipped(new FileSkippedEventArgs(targetFileName));
            return;
        }

        try
        {
            Directory.CreateDirectory(targetDirectory);
            TryDelete(metaPath + ".tmp");

            var resume = await TryLoadResumeMetadataAsync(
                    metaPath,
                    partPath,
                    start,
                    cancellationToken)
                .ConfigureAwait(false);

            _header = start.Header;
            _rawFileName = start.FileName;
            _relativeDirectory = SafeRelativePath.ToWire(start.RelativeDirectory);
            _safeFileName = targetFileName;
            _finalPath = finalPath;
            _partPath = partPath;
            _metaPath = metaPath;
            _overwriteOnFinalMove = _sameNamePolicy == SameNamePolicy.Overwrite;
            _expectedSize = start.Header.FileSize;
            _metadataIntervalBytes = CalculateMetadataInterval(
                start.Header.FileSize);
            _metadataWarningLogged = false;
            _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            long resumeOffset;

            if (resume is not null)
            {
                resumeOffset = resume.PersistedBytes;
                _received = resumeOffset;
                _lastMetadataAttemptReceived = resumeOffset;

                _part = new FileStream(
                    partPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    FileTransferProtocol.BlockSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                if (_part.Length > resumeOffset)
                {
                    _part.SetLength(resumeOffset);
                }

                await _dispatcher
                    .SendAsync(
                        MessageType.ControlMessage,
                        ControlMessageCodec.SerializeResumeRequest(resumeOffset),
                        cancellationToken)
                    .ConfigureAwait(false);

                await HashExistingPartAsync(resumeOffset, cancellationToken)
                    .ConfigureAwait(false);

                _part.Position = resumeOffset;
            }
            else
            {
                resumeOffset = 0;
                _received = 0;
                _lastMetadataAttemptReceived = 0;

                TryDelete(partPath);
                TryDelete(metaPath);
                TryDelete(metaPath + ".tmp");

                _part = new FileStream(
                    partPath,
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    FileTransferProtocol.BlockSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                await PersistMetadataBestEffortAsync(cancellationToken)
                    .ConfigureAwait(false);

                await _dispatcher
                    .SendAsync(
                        MessageType.ControlMessage,
                        ControlMessageCodec.SerializeResumeRequest(0),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            _stopwatch = Stopwatch.StartNew();

            RaiseFileStarted(
                new FileStartedEventArgs(
                    new FileTransferStartInfo(
                        targetFileName,
                        start.Header.FileSize,
                        resumeOffset,
                        _relativeDirectory)));
        }
        catch (Exception exception)
        {
            await SendTransferErrorBestEffortAsync(exception, cancellationToken)
                .ConfigureAwait(false);
            PreserveActive(exception, safeFileName);
        }
    }

    private async Task HashExistingPartAsync(
        long length,
        CancellationToken cancellationToken)
    {
        if (_part is null || _hash is null)
        {
            throw new InvalidOperationException("没有可恢复的 .part 文件。");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(FileTransferProtocol.BlockSize);

        try
        {
            _part.Position = 0;
            var remaining = length;

            while (remaining > 0)
            {
                var count = (int)Math.Min(FileTransferProtocol.BlockSize, remaining);
                var read = await _part
                    .ReadAsync(buffer.AsMemory(0, count), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    throw new TransferTruncatedException(
                        "恢复传输时发现 .part 长度不足。");
                }

                _hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HandleFileChunkAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (_part is null || _hash is null || _header is null)
        {
            return;
        }

        if (payload.Length is <= 0 or > FileTransferProtocol.BlockSize)
        {
            DeleteActive(
                new MessageProtocolException(
                    $"FileChunk payload 长度必须在 1..{FileTransferProtocol.BlockSize} 之间。"),
                _safeFileName);
            return;
        }

        if (_received + payload.Length > _expectedSize)
        {
            DeleteActive(
                new MessageProtocolException(
                    "FileChunk 超过了 FileStart 声明的文件大小。"),
                _safeFileName);
            return;
        }

        try
        {
            await _part
                .WriteAsync(payload, cancellationToken)
                .ConfigureAwait(false);

            // 先把数据 Flush 到 .part，再更新 meta，最后才发送 Ack。
            await _part
                .FlushAsync(cancellationToken)
                .ConfigureAwait(false);

            _hash.AppendData(payload.Span);
            _received += payload.Length;

            // ACK 只表示 .part 已经持久化；不要让它等待 .meta 文件操作。
            await _dispatcher
                .SendAsync(
                    MessageType.ControlMessage,
                    ControlMessageCodec.SerializeAck(_received),
                    cancellationToken)
                .ConfigureAwait(false);

            _progress?.Report(new FileTransferProgress(_received, _expectedSize));

            if (_received - _lastMetadataAttemptReceived >=
                _metadataIntervalBytes)
            {
                await PersistMetadataBestEffortAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            await SendTransferErrorBestEffortAsync(exception, cancellationToken)
                .ConfigureAwait(false);
            PreserveActive(exception, _safeFileName);
        }
    }

    private async Task HandleFileEndAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (_part is null ||
            _hash is null ||
            _header is null ||
            _rawFileName is null ||
            _safeFileName is null ||
            _partPath is null ||
            _metaPath is null ||
            _finalPath is null)
        {
            return;
        }

        if (payload.Length != MessageProtocol.FileEndDigestLength)
        {
            await FailFileEndAsync(
                    new MessageProtocolException(
                        $"FileEnd payload 必须为 {MessageProtocol.FileEndDigestLength} 字节。"),
                    _safeFileName,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (_received != _expectedSize)
        {
            await FailFileEndAsync(
                    new TransferTruncatedException(
                        $"文件接收不完整：预期 {_expectedSize} 字节，实际 {_received} 字节。"),
                    _safeFileName,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var part = _part;
        var hash = _hash;
        var header = _header;
        var safeFileName = _safeFileName;
        var partPath = _partPath;
        var metaPath = _metaPath;
        var finalPath = _finalPath;
        var overwriteOnFinalMove = _overwriteOnFinalMove;
        var elapsed = _stopwatch?.Elapsed ?? TimeSpan.Zero;

        try
        {
            await part.FlushAsync(cancellationToken).ConfigureAwait(false);
            await part.DisposeAsync().ConfigureAwait(false);
            _part = null;

            var actualDigest = hash.GetHashAndReset();
            hash.Dispose();
            _hash = null;

            if (!CryptographicOperations.FixedTimeEquals(
                    actualDigest,
                    payload.Span))
            {
                throw new ChecksumMismatchException();
            }

            var moved = await MovePartToFinalAsync(
                    partPath,
                    finalPath,
                    safeFileName,
                    overwriteOnFinalMove,
                    cancellationToken)
                .ConfigureAwait(false);

            TryDelete(metaPath);
            TryDelete(metaPath + ".tmp");

            try
            {
                await SendFileVerifiedAsync(actualDigest, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception verifyError)
            {
                _log?.Invoke(
                    "发送校验确认失败，文件已保存：" +
                    verifyError.GetType().Name);
            }

            var relativeDirectory = _relativeDirectory;
            ResetState();

            var result = new FileReceiveResult(
                moved.FileName,
                moved.FilePath,
                header.FileSize,
                actualDigest,
                relativeDirectory);

            RaiseFileReceived(new FileReceivedEventArgs(result, elapsed));
        }
        catch (Exception exception)
        {
            await FailFileEndAsync(exception, safeFileName, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void OnDisconnected(object? sender, DisconnectedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (_part is null && _hash is null && _header is null)
        {
            return;
        }

        var message = args.RemoteClosed
            ? "连接已关闭，文件传输未完成；.part 和 .meta 已保留。"
            : "连接异常，文件传输未完成；.part 和 .meta 已保留。";

        PreserveActive(
            new TransferTruncatedException(message, args.Error),
            _safeFileName);
    }

    private void HandleControlMessage(ReadOnlySpan<byte> payload)
    {
        ControlMessage message;

        try
        {
            message = ControlMessageCodec.Deserialize(payload);
        }
        catch (Exception)
        {
            return;
        }

        if (message.Subtype == ControlMessageSubtype.QueueInfo)
        {
            _pendingQueueBytes = message.Offset;
            return;
        }

        if (message.Subtype == ControlMessageSubtype.DirectoryManifest &&
            message.Directories is { Count: > 0 } directories)
        {
            HandleDirectoryManifest(directories);
        }
    }

    private void HandleDirectoryManifest(
        IReadOnlyList<string> directories)
    {
        var created = 0;
        var failed = 0;

        foreach (var directory in directories)
        {
            if (_manifestEntryCount >=
                DirectoryManifestCodec.MaxDirectoriesPerTransfer)
            {
                _log?.Invoke(
                    "目录清单条目数超过上限，已忽略剩余空文件夹。");
                failed += directories.Count - created - failed;
                break;
            }

            _manifestEntryCount++;

            try
            {
                var target = SafeRelativePath.ResolveDirectory(
                    _destinationDirectory,
                    directory);
                Directory.CreateDirectory(target);
                created++;
            }
            catch (Exception exception)
            {
                failed++;
                _log?.Invoke(
                    "创建空文件夹失败：" +
                    $"{directory}（{exception.GetType().Name}）");
            }
        }

        if (created > 0 || failed > 0)
        {
            RaiseDirectoriesReceived(
                new DirectoryManifestEventArgs(created, failed));
        }
    }

    private async Task FailFileEndAsync(
        Exception error,
        string fileName,
        CancellationToken cancellationToken)
    {
        await SendTransferErrorBestEffortAsync(error, cancellationToken)
            .ConfigureAwait(false);
        DeleteActive(error, fileName);
    }

    private async Task SendTransferErrorBestEffortAsync(
        Exception error,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendTransferErrorAsync(error, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception sendError)
        {
            _log?.Invoke(
                "发送传输失败通知失败：" + sendError.GetType().Name);
        }
    }

    private Task SendTransferErrorAsync(
        Exception error,
        CancellationToken cancellationToken)
    {
        var (errorCode, message) = error switch
        {
            FileTransferException transferError =>
                (transferError.ErrorCode, transferError.Message),
            UnauthorizedAccessException =>
                (WifiDirectErrorCode.PermissionDenied,
                 "没有权限写入接收目录。"),
            IOException =>
                (WifiDirectErrorCode.TransferInterrupted,
                 "写入接收目录失败，请检查磁盘空间或文件权限。"),
            _ =>
                (WifiDirectErrorCode.TransferInterrupted,
                 "接收端无法继续接收文件。")
        };

        return _dispatcher.SendAsync(
            MessageType.ControlMessage,
            ControlMessageCodec.SerializeTransferError(
                errorCode,
                message),
            cancellationToken);
    }

    private Task SendFileVerifiedAsync(
        byte[] digest,
        CancellationToken cancellationToken) =>
        _dispatcher.SendAsync(
            MessageType.ControlMessage,
            ControlMessageCodec.SerializeFileVerified(digest),
            cancellationToken);

    private Task SendFileSkippedAsync(CancellationToken cancellationToken) =>
        _dispatcher.SendAsync(
            MessageType.ControlMessage,
            ControlMessageCodec.SerializeFileSkipped(),
            cancellationToken);

    private static bool PathExists(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        return File.Exists(path) || Directory.Exists(path);
    }

    private async Task<(string FileName, string FilePath)> MovePartToFinalAsync(
        string partPath,
        string finalPath,
        string safeFileName,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(partPath, finalPath, overwrite);
                return (safeFileName, finalPath);
            }
            catch (Exception exception)
                when (IsTransientFileError(exception) &&
                      attempt < MetadataWriteAttempts - 1)
            {
                await Task.Delay(
                        MetadataRetryDelayMilliseconds * (attempt + 1),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!overwrite && File.Exists(finalPath))
                {
                    var directory = Path.GetDirectoryName(finalPath) ??
                                    _destinationDirectory;
                    safeFileName = GetAvailableFileName(directory, safeFileName);
                    finalPath = Path.Combine(directory, safeFileName);
                }
            }
        }
    }

    private static string GetAvailableFileName(string directory, string fileName)
    {
        if (!PathExists(directory, fileName))
        {
            return fileName;
        }

        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);

        if (string.IsNullOrEmpty(stem))
        {
            stem = fileName;
            extension = string.Empty;
        }

        for (var index = 1; index < int.MaxValue; index++)
        {
            var candidate = $"{stem} ({index}){extension}";

            if (!PathExists(directory, candidate))
            {
                return candidate;
            }
        }

        throw new IOException("无法生成可用的同名文件名称。");
    }

    private async Task<TransferMetadata?> TryLoadResumeMetadataAsync(
        string metaPath,
        string partPath,
        FileStartPayload start,
        CancellationToken cancellationToken)
    {
        var metadata = await TryReadMetadataAsync(metaPath, cancellationToken)
            .ConfigureAwait(false);

        if (metadata is null)
        {
            return null;
        }

        if (metadata.FileName != start.FileName ||
            metadata.FileSize != start.Header.FileSize ||
            metadata.PersistedBytes < 0 ||
            metadata.PersistedBytes >= start.Header.FileSize ||
            !File.Exists(partPath))
        {
            return null;
        }

        var partLength = new FileInfo(partPath).Length;

        if (partLength < metadata.PersistedBytes)
        {
            return null;
        }

        return metadata;
    }

    private static async Task<TransferMetadata?> TryReadMetadataAsync(
        string metaPath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(metaPath))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(metaPath, cancellationToken)
                .ConfigureAwait(false);

            return TransferMetadataCodec.TryDeserialize(bytes, out var metadata)
                ? metadata
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long CalculateMetadataInterval(long fileSize)
    {
        var interval = fileSize / 128;

        if (interval < MinimumMetadataInterval)
        {
            interval = MinimumMetadataInterval;
        }
        else if (interval > MaximumMetadataInterval)
        {
            interval = MaximumMetadataInterval;
        }

        interval = interval / FileTransferProtocol.BlockSize *
                   FileTransferProtocol.BlockSize;

        return Math.Max(FileTransferProtocol.BlockSize, interval);
    }

    private async Task PersistMetadataBestEffortAsync(
        CancellationToken cancellationToken)
    {
        if (_metaPath is null || _rawFileName is null)
        {
            return;
        }

        var persistedBytes = _received;

        try
        {
            await WriteMetadataWithRetryAsync(persistedBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            TryDelete(_metaPath + ".tmp");

            if (!_metadataWarningLogged)
            {
                _metadataWarningLogged = true;
                _log?.Invoke(
                    "更新断点信息失败，已继续传输：" +
                    exception.GetType().Name);
            }
        }
        finally
        {
            _lastMetadataAttemptReceived = persistedBytes;
        }
    }

    private async Task WriteMetadataWithRetryAsync(
        long persistedBytes,
        CancellationToken cancellationToken)
    {
        if (_metaPath is null || _rawFileName is null)
        {
            return;
        }

        var metaPath = _metaPath;
        var tempPath = metaPath + ".tmp";
        var bytes = TransferMetadataCodec.Serialize(
            new TransferMetadata(
                _rawFileName,
                _expectedSize,
                persistedBytes));

        for (var attempt = 0; attempt < MetadataWriteAttempts; attempt++)
        {
            try
            {
                await using (var stream = new FileStream(
                                 tempPath,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 4096,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await stream.WriteAsync(bytes, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                File.Move(tempPath, metaPath, overwrite: true);
                return;
            }
            catch (Exception exception)
                when (IsTransientFileError(exception) &&
                      attempt < MetadataWriteAttempts - 1)
            {
                await Task.Delay(
                        MetadataRetryDelayMilliseconds,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientFileError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or COMException;

    private void PreserveActive(Exception? error, string? fileName)
    {
        var part = _part;
        var hash = _hash;
        var displayName = fileName ?? _safeFileName;

        ResetState();

        try
        {
            part?.Dispose();
        }
        catch (Exception)
        {
            // 保留原始错误，清理失败不覆盖它。
        }

        try
        {
            hash?.Dispose();
        }
        catch (Exception)
        {
            // 保留原始错误，清理失败不覆盖它。
        }

        if (error is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        RaiseFileFailed(new FileFailedEventArgs(error, displayName));
    }

    private void DeleteActive(Exception error, string? fileName)
    {
        var part = _part;
        var hash = _hash;
        var partPath = _partPath;
        var metaPath = _metaPath;
        var displayName = fileName ?? _safeFileName;

        ResetState();

        try
        {
            part?.Dispose();
        }
        catch (Exception)
        {
            // 保留原始错误，清理失败不覆盖它。
        }

        try
        {
            hash?.Dispose();
        }
        catch (Exception)
        {
            // 保留原始错误，清理失败不覆盖它。
        }

        TryDelete(partPath);
        TryDelete(metaPath);
        TryDelete(metaPath + ".tmp");

        if (Volatile.Read(ref _disposed) == 0)
        {
            RaiseFileFailed(new FileFailedEventArgs(error, displayName));
        }
    }

    private void ResetState()
    {
        _part = null;
        _hash = null;
        _header = null;
        _rawFileName = null;
        _relativeDirectory = string.Empty;
        _safeFileName = null;
        _partPath = null;
        _metaPath = null;
        _finalPath = null;
        _overwriteOnFinalMove = false;
        _received = 0;
        _expectedSize = 0;
        _metadataIntervalBytes = 0;
        _lastMetadataAttemptReceived = 0;
        _metadataWarningLogged = false;
        _stopwatch = null;
    }

    private void RaiseFileStarted(FileStartedEventArgs args)
    {
        try
        {
            FileStarted?.Invoke(this, args);
        }
        catch (Exception)
        {
            // 订阅方异常不应影响消息循环。
        }
    }

    private void RaiseFileReceived(FileReceivedEventArgs args)
    {
        try
        {
            FileReceived?.Invoke(this, args);
        }
        catch (Exception)
        {
            // 订阅方异常不应影响消息循环。
        }
    }

    private void RaiseFileFailed(FileFailedEventArgs args)
    {
        try
        {
            FileFailed?.Invoke(this, args);
        }
        catch (Exception)
        {
            // 订阅方异常不应影响消息循环。
        }
    }

    private void RaiseFileSkipped(FileSkippedEventArgs args)
    {
        try
        {
            FileSkipped?.Invoke(this, args);
        }
        catch (Exception)
        {
            // 订阅方异常不应影响消息循环。
        }
    }

    private void RaiseDirectoriesReceived(DirectoryManifestEventArgs args)
    {
        try
        {
            DirectoriesReceived?.Invoke(this, args);
        }
        catch (Exception)
        {
            // 订阅方异常不应影响消息循环。
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 保留原始传输异常，删除失败不覆盖它。
        }
        catch (UnauthorizedAccessException)
        {
            // 保留原始传输异常，删除失败不覆盖它。
        }
    }
}

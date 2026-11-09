using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;

namespace WifiDirectTransfer.Core;

/// <summary>
/// M1 文件发送端：FileStart → FileChunk(64 KiB) → FileEnd，全部通过 MessageDispatcher 发送。
/// 每个 FileChunk 等待接收端 Ack；FileStart 后等待 ResumeRequest/TransferError/FileSkipped，
/// 2 秒内没有响应则按偏移 0 开始（兼容旧接收端）。
/// 调用方必须已经在 dispatcher 上启动接收循环，否则无法收到 ResumeRequest/Ack。
/// </summary>
public static class FileSender
{
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartResponseTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultVerificationTimeout =
        TimeSpan.FromSeconds(60);

    public static async Task<FileSendResult> SendAsync(
        MessageDispatcher dispatcher,
        Stream source,
        string fileName,
        long maxFileSize = FileTransferProtocol.DefaultMaxFileSize,
        IProgress<FileTransferProgress>? progress = null,
        IProgress<FileTransferStartInfo>? startProgress = null,
        CancellationToken cancellationToken = default,
        Action<string>? log = null,
        TimeSpan? verificationTimeout = null,
        string relativeDirectory = "")
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(relativeDirectory);

        var verifyTimeout = verificationTimeout ?? DefaultVerificationTimeout;

        if (verifyTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(verificationTimeout),
                "校验确认超时必须大于零。");
        }

        if (maxFileSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileSize), "最大文件大小必须大于零。");
        }

        if (!source.CanRead)
        {
            throw new ArgumentException("发送流必须可读。", nameof(source));
        }

        if (!source.CanSeek)
        {
            throw new ArgumentException("发送流必须支持 Length 和 Position。", nameof(source));
        }

        var initialPosition = source.Position;
        var initialLength = source.Length - initialPosition;

        if (initialLength < 0)
        {
            throw new InvalidOperationException("发送流当前位置超过了流长度。");
        }

        if (initialLength > maxFileSize)
        {
            throw new FileTooLargeException(initialLength, maxFileSize);
        }

        var fileNameBytes = FileHeaderCodec.EncodeFileName(fileName);
        var relativeDirectoryBytes = FileHeaderCodec.EncodeRelativeDirectory(
            relativeDirectory);
        var header = FileHeader.CreateFileStart(
            initialLength,
            fileNameBytes.Length,
            relativeDirectoryBytes.Length);
        var codec = new FileHeaderCodec(maxFileSize);
        var startPayload = codec.SerializeFileStartPayload(
            header,
            fileNameBytes,
            relativeDirectoryBytes);

        var controlState = new ControlWaitState();
        dispatcher.MessageReceived += controlState.OnMessageReceivedAsync;

        try
        {
            controlState.BeginStartWait();

            var response = StartResponse.Resume(0);
            var gotResponse = false;

            try
            {
                await dispatcher
                    .SendAsync(MessageType.FileStart, startPayload, cancellationToken)
                    .ConfigureAwait(false);

                try
                {
                    response = await controlState.StartResponseTask
                        .WaitAsync(StartResponseTimeout, cancellationToken)
                        .ConfigureAwait(false);
                    gotResponse = true;
                }
                catch (TimeoutException)
                {
                    response = controlState.CompleteStartWait()
                               ?? StartResponse.Resume(0);
                }
                finally
                {
                    var completed = controlState.CompleteStartWait();

                    if (!gotResponse && completed is not null)
                    {
                        response = completed;
                    }
                }
            }
            finally
            {
                controlState.CompleteStartWait();
            }

            if (response.Error is not null)
            {
                throw response.Error;
            }

            var resumeOffset = response.Offset;

            if (resumeOffset < 0 || resumeOffset > initialLength)
            {
                throw new MessageProtocolException(
                    $"断点偏移 {resumeOffset} 无效，文件大小为 {initialLength} 字节。");
            }

            controlState.ThrowIfLateStart();
            controlState.ThrowIfLateResume();

            startProgress?.Report(
                new FileTransferStartInfo(fileName, initialLength, resumeOffset));

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(FileTransferProtocol.BlockSize);
            var congestionMonitor = new AckCongestionMonitor();
            long processed = 0;

            try
            {
                if (resumeOffset > 0)
                {
                    source.Position = initialPosition;

                    var prefixRemaining = resumeOffset;

                    while (prefixRemaining > 0)
                    {
                        var count = (int)Math.Min(
                            FileTransferProtocol.BlockSize,
                            prefixRemaining);
                        var read = await source
                            .ReadAsync(buffer.AsMemory(0, count), cancellationToken)
                            .ConfigureAwait(false);

                        if (read == 0)
                        {
                            throw new FileChangedException(initialLength, processed);
                        }

                        hash.AppendData(buffer, 0, read);
                        processed += read;
                        prefixRemaining -= read;
                    }

                    controlState.ThrowIfLateStart();
                    controlState.ThrowIfLateResume();
                }

                source.Position = initialPosition + resumeOffset;

                var remaining = initialLength - resumeOffset;

                while (remaining > 0)
                {
                    var count = (int)Math.Min(
                        FileTransferProtocol.BlockSize,
                        remaining);
                    var read = await source
                        .ReadAsync(buffer.AsMemory(0, count), cancellationToken)
                        .ConfigureAwait(false);

                    if (read == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);

                    controlState.ThrowIfLateStart();
                    controlState.BeginAckWait();

                    try
                    {
                        await dispatcher
                            .SendAsync(
                                MessageType.FileChunk,
                                buffer.AsMemory(0, read),
                                cancellationToken)
                            .ConfigureAwait(false);

                        processed += read;

                        long acknowledgedOffset;
                        var ackWatch = Stopwatch.StartNew();

                        try
                        {
                            acknowledgedOffset = await controlState.AckTask
                                .WaitAsync(AckTimeout, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            throw new TransferTimeoutException(
                                "等待接收端 ACK 超时（30 秒）。");
                        }
                        finally
                        {
                            ackWatch.Stop();
                        }

                        if (congestionMonitor.Report(ackWatch.Elapsed))
                        {
                            log?.Invoke(AckCongestionMonitor.WarningMessage);
                        }

                        if (acknowledgedOffset != processed)
                        {
                            throw new MessageProtocolException(
                                $"ACK 偏移不匹配：预期 {processed} 字节，实际 {acknowledgedOffset} 字节。");
                        }
                    }
                    finally
                    {
                        controlState.EndAckWait();
                    }

                    controlState.ThrowIfLateStart();
                    controlState.ThrowIfLateResume();
                    remaining -= read;

                    progress?.Report(
                        new FileTransferProgress(processed, initialLength));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (processed != initialLength)
            {
                throw new FileChangedException(initialLength, processed);
            }

            controlState.ThrowIfLateStart();
            controlState.ThrowIfLateResume();

            var digest = hash.GetHashAndReset();

            controlState.BeginVerifyWait();

            try
            {
                await dispatcher
                    .SendAsync(MessageType.FileEnd, digest, cancellationToken)
                    .ConfigureAwait(false);

                byte[] verifiedDigest;

                try
                {
                    verifiedDigest = await controlState.VerifyTask
                        .WaitAsync(verifyTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new TransferTimeoutException(
                        "等待接收端校验确认超时。");
                }

                if (!CryptographicOperations.FixedTimeEquals(
                        verifiedDigest,
                        digest))
                {
                    throw new MessageProtocolException(
                        "接收端校验确认的 SHA-256 与发送值不一致。");
                }
            }
            finally
            {
                controlState.EndVerifyWait();
            }

            progress?.Report(
                new FileTransferProgress(initialLength, initialLength));

            return new FileSendResult(
                fileName,
                initialLength,
                digest,
                resumeOffset);
        }
        finally
        {
            dispatcher.MessageReceived -= controlState.OnMessageReceivedAsync;
        }
    }

    public static async Task<FileSendResult> SendFileAsync(
        MessageDispatcher dispatcher,
        string filePath,
        long maxFileSize = FileTransferProtocol.DefaultMaxFileSize,
        IProgress<FileTransferProgress>? progress = null,
        IProgress<FileTransferStartInfo>? startProgress = null,
        CancellationToken cancellationToken = default,
        Action<string>? log = null,
        TimeSpan? verificationTimeout = null,
        string relativeDirectory = "")
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fileName = Path.GetFileName(filePath);

        await using var source = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileTransferProtocol.BlockSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await SendAsync(
                dispatcher,
                source,
                fileName,
                maxFileSize,
                progress,
                startProgress,
                cancellationToken,
                log,
                verificationTimeout,
                relativeDirectory)
            .ConfigureAwait(false);
    }

    private sealed record StartResponse(long Offset, Exception? Error)
    {
        public static StartResponse Resume(long offset) => new(offset, null);

        public static StartResponse Fail(Exception error) => new(0, error);
    }

    private sealed class ControlWaitState
    {
        private readonly object _gate = new();

        private TaskCompletionSource<StartResponse>? _startTcs;
        private TaskCompletionSource<long>? _ackTcs;
        private TaskCompletionSource<byte[]>? _verifyTcs;
        private bool _startWaitFinished;
        private bool _lateResume;
        private Exception? _lateStartError;

        public Task<StartResponse> StartResponseTask
        {
            get
            {
                lock (_gate)
                {
                    return _startTcs?.Task
                           ?? Task.FromResult(StartResponse.Resume(0));
                }
            }
        }

        public Task<long> AckTask
        {
            get
            {
                lock (_gate)
                {
                    return _ackTcs?.Task
                           ?? Task.FromException<long>(
                               new InvalidOperationException("尚未开始等待 ACK。"));
                }
            }
        }

        public Task<byte[]> VerifyTask
        {
            get
            {
                lock (_gate)
                {
                    return _verifyTcs?.Task
                           ?? Task.FromException<byte[]>(
                               new InvalidOperationException(
                                   "尚未开始等待校验确认。"));
                }
            }
        }

        public void BeginStartWait()
        {
            lock (_gate)
            {
                _startTcs = CreateStartTcs();
                _startWaitFinished = false;
                _lateResume = false;
                _lateStartError = null;
            }
        }

        public StartResponse? CompleteStartWait()
        {
            lock (_gate)
            {
                _startWaitFinished = true;

                if (_startTcs is { Task.IsCompletedSuccessfully: true })
                {
                    return _startTcs.Task.Result;
                }

                return null;
            }
        }

        public void BeginAckWait()
        {
            lock (_gate)
            {
                _ackTcs = CreateAckTcs();
            }
        }

        public void EndAckWait()
        {
            lock (_gate)
            {
                _ackTcs = null;
            }
        }

        public void BeginVerifyWait()
        {
            lock (_gate)
            {
                _verifyTcs = new TaskCompletionSource<byte[]>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void EndVerifyWait()
        {
            lock (_gate)
            {
                _verifyTcs = null;
            }
        }

        public void ThrowIfLateStart()
        {
            Exception? error;

            lock (_gate)
            {
                error = _lateStartError;
            }

            if (error is not null)
            {
                throw error;
            }
        }

        public void ThrowIfLateResume()
        {
            lock (_gate)
            {
                if (_lateResume)
                {
                    throw new MessageProtocolException(
                        "断点请求在发送开始后才到达，无法安全恢复传输。");
                }
            }
        }

        public Task OnMessageReceivedAsync(
            MessageDispatcher sender,
            MessageReceivedEventArgs args,
            CancellationToken cancellationToken)
        {
            if (args.Type != MessageType.ControlMessage)
            {
                return Task.CompletedTask;
            }

            ControlMessage message;

            try
            {
                message = ControlMessageCodec.Deserialize(args.Payload.Span);
            }
            catch (Exception)
            {
                return Task.CompletedTask;
            }

            Exception? startError = message.Subtype switch
            {
                ControlMessageSubtype.TransferError => new RemoteTransferException(
                    message.ErrorCode,
                    message.Message),
                ControlMessageSubtype.FileSkipped => new FileSkippedException(),
                _ => null
            };

            lock (_gate)
            {
                if (message.Subtype == ControlMessageSubtype.ResumeRequest)
                {
                    if (!_startWaitFinished &&
                        _startTcs is not null &&
                        _startTcs.TrySetResult(StartResponse.Resume(message.Offset)))
                    {
                        return Task.CompletedTask;
                    }

                    _lateResume = true;
                }
                else if (message.Subtype == ControlMessageSubtype.FileVerified)
                {
                    if (message.Digest is { } digest)
                    {
                        _verifyTcs?.TrySetResult(digest);
                    }
                }
                else if (startError is not null)
                {
                    if (!_startWaitFinished &&
                        _startTcs is not null &&
                        _startTcs.TrySetResult(StartResponse.Fail(startError)))
                    {
                        // 已作为启动响应处理。
                    }
                    else
                    {
                        _lateStartError ??= startError;
                    }

                    _ackTcs?.TrySetException(startError);
                    _verifyTcs?.TrySetException(startError);
                }
                else if (message.Subtype == ControlMessageSubtype.Ack)
                {
                    _ackTcs?.TrySetResult(message.Offset);
                }
            }

            return Task.CompletedTask;
        }

        private static TaskCompletionSource<StartResponse> CreateStartTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource<long> CreateAckTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

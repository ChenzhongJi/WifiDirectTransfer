using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.Tests;

internal sealed class TransferTestSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _senderCts =
        new(TimeSpan.FromSeconds(20));
    private readonly CancellationTokenSource _receiverCts =
        new(TimeSpan.FromSeconds(20));

    private int _disposed;

    public TransferTestSession(
        string directory,
        IProgress<FileTransferProgress>? receiverProgress = null,
        SameNamePolicy sameNamePolicy = SameNamePolicy.AutoRename,
        Func<string, long?>? availableFreeSpaceProvider = null)
    {
        Transport = new TestDuplexTransport();
        SenderDispatcher = new MessageDispatcher(
            Transport.SenderInput,
            Transport.SenderOutput);
        ReceiverDispatcher = new MessageDispatcher(
            Transport.ReceiverInput,
            Transport.ReceiverOutput);

        Receiver = new FileReceiver(
            ReceiverDispatcher,
            directory,
            progress: receiverProgress,
            sameNamePolicy: sameNamePolicy,
            availableFreeSpaceProvider: availableFreeSpaceProvider);

        Receiver.FileStarted += (_, args) => Started.TrySetResult(args);
        Receiver.FileReceived += (_, args) => Received.TrySetResult(args.Result);
        Receiver.FileFailed += (_, args) => Failed.TrySetResult(args.Error);
        Receiver.FileSkipped += (_, args) => Skipped.TrySetResult(args);

        SenderLoop = SenderDispatcher.StartReceiveLoopAsync(_senderCts.Token);
        ReceiverLoop = ReceiverDispatcher.StartReceiveLoopAsync(_receiverCts.Token);
    }

    public TestDuplexTransport Transport { get; }

    public MessageDispatcher SenderDispatcher { get; }

    public MessageDispatcher ReceiverDispatcher { get; }

    public FileReceiver Receiver { get; }

    public Task SenderLoop { get; }

    public Task ReceiverLoop { get; }

    public TaskCompletionSource<FileStartedEventArgs> Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<FileReceiveResult> Received { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<Exception> Failed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<FileSkippedEventArgs> Skipped { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<FileTransferStartInfo> SenderStarts { get; } = new();

    public async Task<FileSendResult> SendBytesAsync(
        byte[] data,
        string fileName,
        long maxFileSize = FileTransferProtocol.DefaultMaxFileSize)
    {
        using var source = new MemoryStream(data);
        return await SendAsync(source, fileName, maxFileSize).ConfigureAwait(false);
    }

    public async Task<FileSendResult> SendAsync(
        Stream source,
        string fileName,
        long maxFileSize = FileTransferProtocol.DefaultMaxFileSize)
    {
        var startProgress = new InlineProgress<FileTransferStartInfo>(
            SenderStarts.Add);

        return await FileSender.SendAsync(
                SenderDispatcher,
                source,
                fileName,
                maxFileSize,
                startProgress: startProgress,
                cancellationToken: _senderCts.Token)
            .ConfigureAwait(false);
    }

    public void CompleteTransport() => Transport.CompleteBoth();

    public void CancelSender() => _senderCts.Cancel();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Transport.CompleteBoth();
        _senderCts.Cancel();
        _receiverCts.Cancel();

        try
        {
            await SenderLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 测试清理忽略循环退出异常。
        }

        try
        {
            await ReceiverLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 测试清理忽略循环退出异常。
        }

        await Receiver.DisposeAsync().ConfigureAwait(false);
        await Transport.DisposeAsync().ConfigureAwait(false);

        _senderCts.Dispose();
        _receiverCts.Dispose();
    }
}

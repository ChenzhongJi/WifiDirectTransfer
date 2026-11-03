using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.Tests;

internal sealed class BidirectionalTransferSession : IAsyncDisposable
{
    private readonly TestDuplexTransport _transport = new();
    private readonly CancellationTokenSource _loopsCts =
        new(TimeSpan.FromSeconds(20));
    private readonly Task _leftLoop;
    private readonly Task _rightLoop;
    private int _disposed;

    public BidirectionalTransferSession(
        string leftDirectory,
        string rightDirectory)
    {
        LeftDispatcher = new MessageDispatcher(
            _transport.SenderInput,
            _transport.SenderOutput);
        RightDispatcher = new MessageDispatcher(
            _transport.ReceiverInput,
            _transport.ReceiverOutput);

        LeftReceiver = new FileReceiver(LeftDispatcher, leftDirectory);
        RightReceiver = new FileReceiver(RightDispatcher, rightDirectory);

        _leftLoop = LeftDispatcher.StartReceiveLoopAsync(_loopsCts.Token);
        _rightLoop = RightDispatcher.StartReceiveLoopAsync(_loopsCts.Token);
    }

    public MessageDispatcher LeftDispatcher { get; }

    public MessageDispatcher RightDispatcher { get; }

    public FileReceiver LeftReceiver { get; }

    public FileReceiver RightReceiver { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _transport.CompleteBoth();
        _loopsCts.Cancel();

        try
        {
            await _leftLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 测试清理忽略循环退出异常。
        }

        try
        {
            await _rightLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 测试清理忽略循环退出异常。
        }

        await LeftReceiver.DisposeAsync().ConfigureAwait(false);
        await RightReceiver.DisposeAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _loopsCts.Dispose();
    }
}

namespace WifiDirectTransfer.Core;

public sealed record HelloMessage(
    int ProtocolVersion,
    int MaxPayloadLength,
    string Build);

/// <summary>
/// 连接建立后的版本协商。必须在启动接收循环之前创建，确保不会漏掉对端的 Hello。
/// </summary>
public sealed class ConnectionHandshake : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly MessageDispatcher _dispatcher;
    private readonly string _localBuild;
    private readonly int _localMaxPayloadLength;
    private readonly TimeSpan _timeout;
    private readonly TaskCompletionSource<HelloMessage> _peerHello =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _disposed;
    private int _started;

    public ConnectionHandshake(
        MessageDispatcher dispatcher,
        string localBuild,
        int localMaxPayloadLength,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(localBuild);

        if (localMaxPayloadLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localMaxPayloadLength),
                "单帧上限必须大于零。");
        }

        _timeout = timeout ?? DefaultTimeout;

        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "协商超时必须大于零。");
        }

        _dispatcher = dispatcher;
        _localBuild = localBuild;
        _localMaxPayloadLength = localMaxPayloadLength;

        // 先订阅再启动接收循环，避免对端的 Hello 在订阅前到达而被丢弃。
        _dispatcher.MessageReceived += OnMessageReceivedAsync;
    }

    public async Task<HelloMessage> ExchangeAsync(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("版本协商已经启动。");
        }

        await _dispatcher
            .SendAsync(
                MessageType.ControlMessage,
                ControlMessageCodec.SerializeHello(
                    MessageProtocol.ProtocolVersion,
                    _localMaxPayloadLength,
                    _localBuild),
                cancellationToken)
            .ConfigureAwait(false);

        HelloMessage peerHello;

        try
        {
            peerHello = await _peerHello.Task
                .WaitAsync(_timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new HandshakeTimeoutException();
        }

        if (peerHello.ProtocolVersion != MessageProtocol.ProtocolVersion)
        {
            throw new ProtocolVersionMismatchException(
                MessageProtocol.ProtocolVersion,
                peerHello.ProtocolVersion);
        }

        return peerHello;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _dispatcher.MessageReceived -= OnMessageReceivedAsync;
        _peerHello.TrySetCanceled();
    }

    private Task OnMessageReceivedAsync(
        MessageDispatcher sender,
        MessageReceivedEventArgs args,
        CancellationToken cancellationToken)
    {
        if (args.Type != MessageType.ControlMessage)
        {
            return Task.CompletedTask;
        }

        try
        {
            var message = ControlMessageCodec.Deserialize(args.Payload.Span);

            if (message.Subtype == ControlMessageSubtype.Hello)
            {
                _peerHello.TrySetResult(
                    new HelloMessage(
                        message.ProtocolVersion,
                        message.MaxPayloadLength,
                        message.Message));
            }
        }
        catch (Exception)
        {
            // 协商阶段的其他控制消息或坏帧直接忽略。
        }

        return Task.CompletedTask;
    }
}

namespace WifiDirectTransfer.Core;

public sealed class MessageReceivedEventArgs : EventArgs
{
    public MessageReceivedEventArgs(MessageType type, ReadOnlyMemory<byte> payload)
    {
        Type = type;
        Payload = payload;
    }

    public MessageType Type { get; }

    /// <summary>
    /// 仅在 MessageReceived 处理器执行期间有效，处理器不应在返回后继续持有。
    /// </summary>
    public ReadOnlyMemory<byte> Payload { get; }
}

public sealed class DisconnectedEventArgs : EventArgs
{
    public DisconnectedEventArgs(Exception? error, bool remoteClosed)
    {
        Error = error;
        RemoteClosed = remoteClosed;
    }

    public Exception? Error { get; }

    public bool RemoteClosed { get; }
}

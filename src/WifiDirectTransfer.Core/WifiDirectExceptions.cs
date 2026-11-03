namespace WifiDirectTransfer.Core;

/// <summary>
/// Wi-Fi Direct 连接的领域异常基类。
/// </summary>
public class WifiDirectException : Exception
{
    public WifiDirectException(
        WifiDirectErrorCode errorCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public WifiDirectErrorCode ErrorCode { get; }
}

public sealed class WifiDirectUnsupportedException : WifiDirectException
{
    public WifiDirectUnsupportedException(
        string message,
        WifiDirectErrorCode errorCode = WifiDirectErrorCode.WifiDirectNotSupported,
        Exception? innerException = null)
        : base(errorCode, message, innerException)
    {
    }
}

public sealed class DiscoveryTimeoutException : WifiDirectException
{
    public DiscoveryTimeoutException(
        string message = "搜索超时，未发现可用设备。",
        Exception? innerException = null)
        : base(WifiDirectErrorCode.DiscoveryTimeout, message, innerException)
    {
    }
}

public sealed class ConnectionRejectedException : WifiDirectException
{
    public ConnectionRejectedException(
        string message = "对方拒绝或未在时限内接受连接。",
        Exception? innerException = null)
        : base(WifiDirectErrorCode.ConnectionRejected, message, innerException)
    {
    }
}

public sealed class ConnectionTimeoutException : WifiDirectException
{
    public ConnectionTimeoutException(
        string message = "连接超时，请确认对方设备已打开接收。",
        Exception? innerException = null)
        : base(WifiDirectErrorCode.ConnectionTimeout, message, innerException)
    {
    }
}

public sealed class TransferTimeoutException : WifiDirectException
{
    public TransferTimeoutException(
        string message = "传输超时，请重试。",
        Exception? innerException = null)
        : base(WifiDirectErrorCode.TransferTimeout, message, innerException)
    {
    }
}

public sealed class WifiDirectConnectionFailedException : WifiDirectException
{
    public WifiDirectConnectionFailedException(
        string message = "无法建立 Wi-Fi Direct 连接，请重试。",
        Exception? innerException = null)
        : base(WifiDirectErrorCode.ConnectionFailed, message, innerException)
    {
    }
}

public sealed class WifiDirectPeerNotFoundException : WifiDirectException
{
    public WifiDirectPeerNotFoundException(
        WifiDirectPeer peer,
        Exception? innerException = null)
        : base(
            WifiDirectErrorCode.PeerNotFound,
            $"设备“{peer.DisplayName}”的信息已过期，请重新扫描后再试。",
            innerException)
    {
        Peer = peer;
    }

    public WifiDirectPeer Peer { get; }
}

public sealed class WifiDirectInvalidStateException : WifiDirectException
{
    public WifiDirectInvalidStateException(
        string message,
        Exception? innerException = null)
        : base(WifiDirectErrorCode.InvalidState, message, innerException)
    {
    }
}

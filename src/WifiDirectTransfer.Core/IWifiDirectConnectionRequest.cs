namespace WifiDirectTransfer.Core;

/// <summary>
/// 接收端收到的一次连接请求。拒绝时不要调用 AcceptAsync。
/// </summary>
public interface IWifiDirectConnectionRequest
{
    WifiDirectPeer Peer { get; }

    DateTimeOffset ReceivedAt { get; }
}

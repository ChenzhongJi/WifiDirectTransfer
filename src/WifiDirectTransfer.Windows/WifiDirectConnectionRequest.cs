using WifiDirectTransfer.Core;
using Windows.Networking.Proximity;

namespace WifiDirectTransfer.Windows;

/// <summary>
/// ConnectionRequested 事件的请求快照。拒绝时只需不调用 AcceptAsync。
/// </summary>
internal sealed class WifiDirectConnectionRequest : IWifiDirectConnectionRequest
{
    private int _acceptStarted;

    public WifiDirectConnectionRequest(
        WifiDirectPeer peer,
        PeerInformation peerInformation,
        DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(peerInformation);

        Peer = peer;
        PeerInformation = peerInformation;
        ReceivedAt = receivedAt;
    }

    public WifiDirectPeer Peer { get; }

    public DateTimeOffset ReceivedAt { get; }

    internal PeerInformation PeerInformation { get; }

    internal bool TryBeginAccept() => Interlocked.Exchange(ref _acceptStarted, 1) == 0;
}

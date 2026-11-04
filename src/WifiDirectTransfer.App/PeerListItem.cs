using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

internal sealed class PeerListItem
{
    public PeerListItem(WifiDirectPeer peer, KnownDevice? knownDevice)
    {
        ArgumentNullException.ThrowIfNull(peer);

        Peer = peer;
        KnownDevice = knownDevice;
    }

    public WifiDirectPeer Peer { get; }

    public KnownDevice? KnownDevice { get; }

    public bool IsKnown => KnownDevice is not null;

    public string DisplayName => Peer.DisplayName;

    public string LastConnectedText => KnownDevice is null
        ? "已发现"
        : $"上次连接 {KnownDevice.LastConnectedAt.LocalDateTime:MM-dd HH:mm}";

    public string DisplayNameText => IsKnown
        ? $"{Peer.DisplayName}（最近连接）"
        : Peer.DisplayName;

    public string DetailText => KnownDevice is null
        ? Peer.Id
        : $"{Peer.Id} · 最后连接 {KnownDevice.LastConnectedAt.LocalDateTime:yyyy-MM-dd HH:mm}";
}

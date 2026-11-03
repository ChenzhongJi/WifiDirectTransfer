namespace WifiDirectTransfer.Core;

/// <summary>
/// 发现结果中的对端快照。Id 用于在本次服务实例内关联底层 PeerInformation。
/// </summary>
public sealed record WifiDirectPeer(string Id, string DisplayName);

namespace WifiDirectTransfer.Core;

/// <summary>
/// Wi-Fi Direct 对端身份匹配规则。某些网卡/驱动在每次扫描时返回的
/// PeerInformation.Id 会变化，因此需要允许按唯一显示名称回退匹配。
/// </summary>
public static class PeerIdentityMatcher
{
    public const string UnnamedPeerDisplayName = "未命名设备";

    public static bool IsSameDisplayName(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) ||
            string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var leftName = left.Trim();
        var rightName = right.Trim();

        if (string.Equals(
                leftName,
                UnnamedPeerDisplayName,
                StringComparison.Ordinal) ||
            string.Equals(
                rightName,
                UnnamedPeerDisplayName,
                StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(
            leftName,
            rightName,
            StringComparison.OrdinalIgnoreCase);
    }

    public static WifiDirectPeer? FindUniqueByName(
        IEnumerable<WifiDirectPeer> peers,
        string? displayName)
    {
        ArgumentNullException.ThrowIfNull(peers);

        var matches = peers
            .Where(peer => IsSameDisplayName(peer.DisplayName, displayName))
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }
}

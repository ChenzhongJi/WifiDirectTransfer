namespace WifiDirectTransfer.Core;

/// <summary>
/// 根据握手携带的对端构建号决定本端发送时允许的最大文件大小。
/// 1.1.x 接收端仍只接受 2 GiB，提前限制可以避免大文件传输到最后才失败。
/// </summary>
public static class PeerBuildCompatibility
{
    public static long GetMaxFileSize(string? peerBuild)
    {
        if (string.IsNullOrWhiteSpace(peerBuild))
        {
            return FileTransferProtocol.LegacyMaxFileSize;
        }

        var versionText = peerBuild.Trim();
        var separator = versionText.IndexOfAny(new[] { '+', '-' });

        if (separator >= 0)
        {
            versionText = versionText[..separator];
        }

        if (!Version.TryParse(versionText, out var version))
        {
            return FileTransferProtocol.LegacyMaxFileSize;
        }

        return version.Major > 1 ||
               (version.Major == 1 && version.Minor >= 2)
            ? FileTransferProtocol.DefaultMaxFileSize
            : FileTransferProtocol.LegacyMaxFileSize;
    }
}

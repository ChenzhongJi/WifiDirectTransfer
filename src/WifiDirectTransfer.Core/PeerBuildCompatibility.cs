namespace WifiDirectTransfer.Core;

/// <summary>
/// 根据握手携带的对端构建号决定 1.2+ 的可选能力。
/// 1.1.x 接收端仍只接受 2 GiB，提前限制可以避免大文件传输到最后才失败。
/// </summary>
public static class PeerBuildCompatibility
{
    public static long GetMaxFileSize(string? peerBuild)
    {
        if (!TryGetVersion(peerBuild, out var version))
        {
            return FileTransferProtocol.LegacyMaxFileSize;
        }

        return version.Major > 1 ||
               (version.Major == 1 && version.Minor >= 2)
            ? FileTransferProtocol.DefaultMaxFileSize
            : FileTransferProtocol.LegacyMaxFileSize;
    }

    /// <summary>
    /// 1.3+ 支持 DirectoryManifest 扩展。未知构建号按不支持处理，避免向旧端发送未知控制消息。
    /// </summary>
    public static bool SupportsDirectoryManifest(string? peerBuild)
    {
        return TryGetVersion(peerBuild, out var version) &&
               (version.Major > 1 ||
                (version.Major == 1 && version.Minor >= 3));
    }

    private static bool TryGetVersion(
        string? build,
        out Version version)
    {
        version = new Version(0, 0);

        if (string.IsNullOrWhiteSpace(build))
        {
            return false;
        }

        var versionText = build.Trim();
        var separator = versionText.IndexOfAny(new[] { '+', '-' });

        if (separator >= 0)
        {
            versionText = versionText[..separator];
        }

        return Version.TryParse(versionText, out version!);
    }
}

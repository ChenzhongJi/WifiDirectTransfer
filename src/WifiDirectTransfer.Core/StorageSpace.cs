namespace WifiDirectTransfer.Core;

/// <summary>
/// 查询目标目录所在卷的可用空间。无法确定时返回 null，调用方应放行传输。
/// </summary>
public static class StorageSpace
{
    public static long? GetAvailableFreeSpace(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        try
        {
            var fullPath = Path.GetFullPath(directory);
            var root = Path.GetPathRoot(fullPath);

            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);

            return drive.IsReady
                ? Math.Max(0, drive.AvailableFreeSpace)
                : null;
        }
        catch (Exception)
        {
            // 网络路径、已移除的驱动器或权限问题：无法预检时不应阻止传输。
            return null;
        }
    }
}

namespace WifiDirectTransfer.Core;

/// <summary>接收端处理完一组目录清单后的结果。</summary>
public sealed class DirectoryManifestEventArgs : EventArgs
{
    public DirectoryManifestEventArgs(
        int createdCount,
        int failedCount,
        IReadOnlyList<string>? createdDirectories = null)
    {
        CreatedCount = Math.Max(0, createdCount);
        FailedCount = Math.Max(0, failedCount);
        CreatedDirectories = createdDirectories ??
                             Array.Empty<string>();
    }

    public int CreatedCount { get; }

    public int FailedCount { get; }

    /// <summary>成功创建或复用的相对目录，用于接收列表按顶层项目归组。</summary>
    public IReadOnlyList<string> CreatedDirectories { get; }
}

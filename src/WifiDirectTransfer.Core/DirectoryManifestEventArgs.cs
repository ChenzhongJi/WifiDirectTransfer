namespace WifiDirectTransfer.Core;

/// <summary>接收端处理完一组目录清单后的结果。</summary>
public sealed class DirectoryManifestEventArgs : EventArgs
{
    public DirectoryManifestEventArgs(int createdCount, int failedCount)
    {
        CreatedCount = Math.Max(0, createdCount);
        FailedCount = Math.Max(0, failedCount);
    }

    public int CreatedCount { get; }

    public int FailedCount { get; }
}

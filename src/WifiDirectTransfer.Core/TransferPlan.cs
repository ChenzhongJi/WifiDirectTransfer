namespace WifiDirectTransfer.Core;

/// <summary>
/// 一次添加操作产生的发送计划：文件列表和需要在接收端创建的空文件夹。
/// 空文件夹路径使用协议正斜杠格式，相对于接收目录根。
/// </summary>
public sealed class TransferPlan
{
    public TransferPlan(
        IReadOnlyList<TransferSource> files,
        IReadOnlyList<string> emptyDirectories)
    {
        Files = files ?? throw new ArgumentNullException(nameof(files));
        EmptyDirectories = emptyDirectories ??
                           throw new ArgumentNullException(nameof(emptyDirectories));
    }

    public IReadOnlyList<TransferSource> Files { get; }

    public IReadOnlyList<string> EmptyDirectories { get; }

    public bool HasFiles => Files.Count > 0;

    public bool HasEmptyDirectories => EmptyDirectories.Count > 0;

    public bool HasWork => HasFiles || HasEmptyDirectories;

    public static TransferPlan Empty { get; } = new(
        Array.Empty<TransferSource>(),
        Array.Empty<string>());
}

/// <summary>
/// 入队结果。SkippedEmptyDirectoryCount 表示对端版本低于 1.3 时被跳过的空文件夹数量。
/// </summary>
public sealed record QueueEnqueueResult(
    IReadOnlyList<PendingFile> Files,
    int EmptyDirectoryCount,
    int SkippedEmptyDirectoryCount)
{
    public bool HasWork =>
        Files.Count > 0 || EmptyDirectoryCount > 0;
}

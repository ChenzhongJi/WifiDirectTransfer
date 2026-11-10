namespace WifiDirectTransfer.Core;

/// <summary>
/// 一个顶层发送项目：直接选择的文件，或用户选择的一个文件夹。
/// 文件夹组会保留组内文件顺序和空文件夹清单。
/// </summary>
public sealed class TransferGroupPlan
{
    public TransferGroupPlan(
        string displayName,
        bool isFolder,
        IReadOnlyList<TransferSource> files,
        IReadOnlyList<string> emptyDirectories,
        string? sourcePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(emptyDirectories);

        DisplayName = displayName;
        IsFolder = isFolder;
        Files = files;
        EmptyDirectories = emptyDirectories;
        SourcePath = sourcePath ?? files.FirstOrDefault()?.SourcePath;
    }

    public string DisplayName { get; }

    public bool IsFolder { get; }

    public IReadOnlyList<TransferSource> Files { get; }

    public IReadOnlyList<string> EmptyDirectories { get; }

    public string? SourcePath { get; }
}

/// <summary>
/// 一次添加操作产生的发送计划：文件列表、空文件夹和顶层项目分组。
/// 空文件夹路径使用协议正斜杠格式，相对于接收目录根。
/// </summary>
public sealed class TransferPlan
{
    public TransferPlan(
        IReadOnlyList<TransferSource> files,
        IReadOnlyList<string> emptyDirectories,
        IReadOnlyList<TransferGroupPlan>? groups = null)
    {
        Files = files ?? throw new ArgumentNullException(nameof(files));
        EmptyDirectories = emptyDirectories ??
                           throw new ArgumentNullException(nameof(emptyDirectories));
        Groups = groups ?? BuildGroups(Files, EmptyDirectories);
    }

    public IReadOnlyList<TransferSource> Files { get; }

    public IReadOnlyList<string> EmptyDirectories { get; }

    public IReadOnlyList<TransferGroupPlan> Groups { get; }

    public bool HasFiles => Files.Count > 0;

    public bool HasEmptyDirectories => EmptyDirectories.Count > 0;

    public bool HasWork =>
        HasFiles || HasEmptyDirectories || Groups.Count > 0;

    public static TransferPlan Empty { get; } = new(
        Array.Empty<TransferSource>(),
        Array.Empty<string>(),
        Array.Empty<TransferGroupPlan>());

    private static IReadOnlyList<TransferGroupPlan> BuildGroups(
        IReadOnlyList<TransferSource> files,
        IReadOnlyList<string> emptyDirectories)
    {
        var builders = new List<GroupBuilder>();
        var byRoot = new Dictionary<string, GroupBuilder>(
            StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var root = GetRootSegment(file.RelativeDirectory);

            if (root.Length == 0)
            {
                var name = GetUniqueName(file.FileName, usedNames);
                builders.Add(
                    new GroupBuilder(
                        name,
                        isFolder: false,
                        file.SourcePath));
                builders[^1].Files.Add(file);
                continue;
            }

            if (!byRoot.TryGetValue(root, out var builder))
            {
                builder = new GroupBuilder(
                    GetUniqueName(root, usedNames),
                    isFolder: true,
                    file.SourcePath);
                byRoot.Add(root, builder);
                builders.Add(builder);
            }

            builder.Files.Add(file);
        }

        foreach (var directory in emptyDirectories)
        {
            var root = GetRootSegment(directory);

            if (root.Length == 0)
            {
                continue;
            }

            if (!byRoot.TryGetValue(root, out var builder))
            {
                builder = new GroupBuilder(
                    GetUniqueName(root, usedNames),
                    isFolder: true,
                    sourcePath: null);
                byRoot.Add(root, builder);
                builders.Add(builder);
            }

            builder.EmptyDirectories.Add(directory);
        }

        return builders
            .Select(builder => new TransferGroupPlan(
                builder.DisplayName,
                builder.IsFolder,
                builder.Files,
                builder.EmptyDirectories,
                builder.SourcePath))
            .ToArray();
    }

    private static string GetRootSegment(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var normalized = relativePath.Replace('\\', '/');
        var separator = normalized.IndexOf('/');
        return separator < 0
            ? normalized
            : normalized[..separator];
    }

    private static string GetUniqueName(
        string baseName,
        HashSet<string> usedNames)
    {
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "项目";
        }

        if (usedNames.Add(baseName))
        {
            return baseName;
        }

        for (var index = 2; index < int.MaxValue; index++)
        {
            var candidate = $"{baseName} ({index})";

            if (usedNames.Add(candidate))
            {
                return candidate;
            }
        }

        return baseName;
    }

    private sealed class GroupBuilder
    {
        public GroupBuilder(
            string displayName,
            bool isFolder,
            string? sourcePath)
        {
            DisplayName = displayName;
            IsFolder = isFolder;
            SourcePath = sourcePath;
        }

        public string DisplayName { get; }

        public bool IsFolder { get; }

        public string? SourcePath { get; }

        public List<TransferSource> Files { get; } = new();

        public List<string> EmptyDirectories { get; } = new();
    }
}

/// <summary>
/// 入队结果。SkippedEmptyDirectoryCount 表示对端版本低于 1.3 时被跳过的空文件夹数量。
/// </summary>
public sealed record QueueEnqueueResult(
    IReadOnlyList<PendingFile> Files,
    int EmptyDirectoryCount,
    int SkippedEmptyDirectoryCount,
    IReadOnlyList<QueueGroup>? Groups = null)
{
    public IReadOnlyList<QueueGroup> AddedGroups =>
        Groups ?? Array.Empty<QueueGroup>();

    public bool HasWork =>
        Files.Count > 0 || EmptyDirectoryCount > 0;
}

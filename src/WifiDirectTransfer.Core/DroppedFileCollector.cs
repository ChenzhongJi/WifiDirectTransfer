namespace WifiDirectTransfer.Core;

/// <summary>
/// 将拖入的文件和文件夹展开为发送计划。
/// 每个直接选择的文件或文件夹都会形成一个顶层项目分组。
/// </summary>
public static class DroppedFileCollector
{
    public static IReadOnlyList<string> Collect(
        IEnumerable<string> paths,
        Action<string>? log = null)
    {
        return CollectPlan(paths, log)
            .Files
            .Select(item => item.SourcePath)
            .ToArray();
    }

    public static IReadOnlyList<TransferSource> CollectItems(
        IEnumerable<string> paths,
        Action<string>? log = null)
    {
        return CollectPlan(paths, log).Files;
    }

    public static TransferPlan CollectPlan(
        IEnumerable<string> paths,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var files = new List<TransferSource>();
        var emptyDirectories = new List<string>();
        var groups = new List<TransferGroupPlan>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenEmptyDirectories = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var usedRootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedGroupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                if (File.Exists(path))
                {
                    var fullPath = Path.GetFullPath(path);

                    if (!seenFiles.Add(fullPath))
                    {
                        continue;
                    }

                    var fileName = Path.GetFileName(fullPath);
                    var source = new TransferSource(
                        fullPath,
                        fileName,
                        string.Empty);
                    files.Add(source);
                    groups.Add(
                        new TransferGroupPlan(
                            GetUniqueGroupName(fileName, usedGroupNames),
                            isFolder: false,
                            new[] { source },
                            Array.Empty<string>(),
                            fullPath));
                }
                else if (Directory.Exists(path))
                {
                    var groupFiles = new List<TransferSource>();
                    var groupEmptyDirectories = new List<string>();
                    var rootName = CollectDirectory(
                        path,
                        groupFiles,
                        groupEmptyDirectories,
                        seenFiles,
                        seenEmptyDirectories,
                        usedRootNames,
                        log);

                    if (rootName is null ||
                        (groupFiles.Count == 0 &&
                         groupEmptyDirectories.Count == 0))
                    {
                        continue;
                    }

                    files.AddRange(groupFiles);
                    emptyDirectories.AddRange(groupEmptyDirectories);
                    groups.Add(
                        new TransferGroupPlan(
                            GetUniqueGroupName(rootName, usedGroupNames),
                            isFolder: true,
                            groupFiles,
                            groupEmptyDirectories,
                            groupFiles.FirstOrDefault()?.SourcePath));
                }
                else
                {
                    log?.Invoke($"拖入路径不存在，已跳过：{path}");
                }
            }
            catch (Exception exception)
                when (exception is IOException
                      or UnauthorizedAccessException
                      or ArgumentException
                      or NotSupportedException)
            {
                log?.Invoke(
                    $"展开拖入路径失败：{path}" +
                    $"（{exception.GetType().Name}）");
            }
        }

        return new TransferPlan(files, emptyDirectories, groups);
    }

    private static string? CollectDirectory(
        string path,
        List<TransferSource> files,
        List<string> emptyDirectories,
        HashSet<string> seenFiles,
        HashSet<string> seenEmptyDirectories,
        HashSet<string> usedRootNames,
        Action<string>? log)
    {
        var initialFileCount = files.Count;
        var initialEmptyDirectoryCount = emptyDirectories.Count;
        var fullRoot = Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var rootName = GetUniqueRootName(
            GetRootName(fullRoot),
            usedRootNames);
        var nonEmptyDirectories = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(
                     fullRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            var fullPath = Path.GetFullPath(file);

            if (!seenFiles.Add(fullPath))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrEmpty(directory))
            {
                MarkNonEmptyDirectories(
                    nonEmptyDirectories,
                    directory,
                    fullRoot);
            }

            var relativePath = Path.GetRelativePath(fullRoot, fullPath);
            var relativeDirectory = Path.GetDirectoryName(relativePath) ??
                                    string.Empty;
            var destinationDirectory = string.IsNullOrEmpty(relativeDirectory)
                ? rootName
                : Path.Combine(rootName, relativeDirectory);

            files.Add(
                new TransferSource(
                    fullPath,
                    Path.GetFileName(fullPath),
                    destinationDirectory));
        }

        var subdirectories = Directory.EnumerateDirectories(
                fullRoot,
                "*",
                SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .ToArray();

        if (subdirectories.Length == 0 &&
            !nonEmptyDirectories.Contains(fullRoot))
        {
            AddEmptyDirectory(
                rootName,
                emptyDirectories,
                seenEmptyDirectories,
                log);
        }

        foreach (var subdirectory in subdirectories)
        {
            if (nonEmptyDirectories.Contains(subdirectory))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(fullRoot, subdirectory);
            var destinationDirectory = Path.Combine(rootName, relativePath);

            AddEmptyDirectory(
                destinationDirectory,
                emptyDirectories,
                seenEmptyDirectories,
                log);
        }

        if (files.Count == initialFileCount &&
            emptyDirectories.Count > initialEmptyDirectoryCount)
        {
            log?.Invoke($"文件夹中没有文件，将在接收端创建空文件夹：{path}");
        }

        return files.Count > initialFileCount ||
               emptyDirectories.Count > initialEmptyDirectoryCount
            ? rootName
            : null;
    }

    private static void MarkNonEmptyDirectories(
        HashSet<string> nonEmptyDirectories,
        string directory,
        string fullRoot)
    {
        var current = Path.GetFullPath(directory);

        while (true)
        {
            if (!nonEmptyDirectories.Add(current))
            {
                break;
            }

            if (string.Equals(
                    current,
                    fullRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var parent = Path.GetDirectoryName(current);

            if (string.IsNullOrEmpty(parent))
            {
                break;
            }

            current = parent;
        }
    }

    private static void AddEmptyDirectory(
        string destinationDirectory,
        List<string> emptyDirectories,
        HashSet<string> seenEmptyDirectories,
        Action<string>? log)
    {
        try
        {
            var wirePath = SafeRelativePath.ToWire(destinationDirectory);

            if (wirePath.Length == 0 || !seenEmptyDirectories.Add(wirePath))
            {
                return;
            }

            emptyDirectories.Add(wirePath);
        }
        catch (Exception exception)
            when (exception is InvalidFileHeaderException
                  or ArgumentException
                  or NotSupportedException)
        {
            log?.Invoke(
                "跳过无法发送的空文件夹：" +
                $"{destinationDirectory}（{exception.GetType().Name}）");
        }
    }

    private static string GetRootName(string fullRoot)
    {
        var name = Path.GetFileName(fullRoot);

        if (!string.IsNullOrWhiteSpace(name))
        {
            return SafeFileName.Sanitize(name);
        }

        var root = Path.GetPathRoot(fullRoot) ?? string.Empty;
        name = root.TrimEnd('\\', '/')
            .Replace(":", string.Empty, StringComparison.Ordinal);

        return string.IsNullOrWhiteSpace(name)
            ? "folder"
            : SafeFileName.Sanitize(name);
    }

    private static string GetUniqueRootName(
        string rootName,
        HashSet<string> usedRootNames)
    {
        if (usedRootNames.Add(rootName))
        {
            return rootName;
        }

        for (var index = 2; index < int.MaxValue; index++)
        {
            var candidate = $"{rootName} ({index})";

            if (usedRootNames.Add(candidate))
            {
                return candidate;
            }
        }

        return rootName;
    }

    private static string GetUniqueGroupName(
        string groupName,
        HashSet<string> usedGroupNames)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            groupName = "项目";
        }

        if (usedGroupNames.Add(groupName))
        {
            return groupName;
        }

        for (var index = 2; index < int.MaxValue; index++)
        {
            var candidate = $"{groupName} ({index})";

            if (usedGroupNames.Add(candidate))
            {
                return candidate;
            }
        }

        return groupName;
    }
}

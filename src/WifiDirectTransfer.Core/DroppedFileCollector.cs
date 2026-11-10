namespace WifiDirectTransfer.Core;

/// <summary>
/// 将拖入的文件和文件夹展开为发送计划。
/// 文件夹保留根目录名和子目录结构；1.3 起会记录需要创建的空文件夹。
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
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenEmptyDirectories = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var usedRootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                    if (seenFiles.Add(fullPath))
                    {
                        files.Add(
                            new TransferSource(
                                fullPath,
                                Path.GetFileName(fullPath),
                                string.Empty));
                    }
                }
                else if (Directory.Exists(path))
                {
                    CollectDirectory(
                        path,
                        files,
                        emptyDirectories,
                        seenFiles,
                        seenEmptyDirectories,
                        usedRootNames,
                        log);
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

        return new TransferPlan(files, emptyDirectories);
    }

    private static void CollectDirectory(
        string path,
        List<TransferSource> files,
        List<string> emptyDirectories,
        HashSet<string> seenFiles,
        HashSet<string> seenEmptyDirectories,
        HashSet<string> usedRootNames,
        Action<string>? log)
    {
        var fullRoot = Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var rootName = GetUniqueRootName(
            GetRootName(fullRoot),
            usedRootNames);
        var nonEmptyDirectories = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var includedFiles = 0;
        var emptyDirectoryCountBefore = emptyDirectories.Count;

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

            includedFiles++;
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

        if (includedFiles == 0 &&
            emptyDirectories.Count > emptyDirectoryCountBefore)
        {
            log?.Invoke($"文件夹中没有文件，将在接收端创建空文件夹：{path}");
        }
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
}

namespace WifiDirectTransfer.Core;

/// <summary>
/// 将拖入的文件和文件夹展开为带相对目录的传输源列表。
/// 文件夹会保留根目录名和子目录结构；空目录暂不发送。
/// </summary>
public static class DroppedFileCollector
{
    public static IReadOnlyList<string> Collect(
        IEnumerable<string> paths,
        Action<string>? log = null)
    {
        return CollectItems(paths, log)
            .Select(item => item.SourcePath)
            .ToArray();
    }

    public static IReadOnlyList<TransferSource> CollectItems(
        IEnumerable<string> paths,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var items = new List<TransferSource>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                        items.Add(
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
                        items,
                        seenFiles,
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

        return items;
    }

    private static void CollectDirectory(
        string path,
        List<TransferSource> items,
        HashSet<string> seenFiles,
        HashSet<string> usedRootNames,
        Action<string>? log)
    {
        var fullRoot = Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var rootName = GetUniqueRootName(
            GetRootName(fullRoot),
            usedRootNames);
        var foundFile = false;

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

            foundFile = true;
            var relativePath = Path.GetRelativePath(fullRoot, fullPath);
            var relativeDirectory = Path.GetDirectoryName(relativePath) ??
                                    string.Empty;
            var destinationDirectory = string.IsNullOrEmpty(relativeDirectory)
                ? rootName
                : Path.Combine(rootName, relativeDirectory);

            items.Add(
                new TransferSource(
                    fullPath,
                    Path.GetFileName(fullPath),
                    destinationDirectory));
        }

        if (!foundFile)
        {
            log?.Invoke($"文件夹为空，暂不发送：{path}");
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

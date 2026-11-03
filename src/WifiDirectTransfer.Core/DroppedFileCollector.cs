namespace WifiDirectTransfer.Core;

/// <summary>
/// 将拖入的文件和文件夹展开为文件路径列表。文件夹递归展开并与现有文件去重。
/// </summary>
public static class DroppedFileCollector
{
    public static IReadOnlyList<string> Collect(
        IEnumerable<string> paths,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var files = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                    if (seenPaths.Add(fullPath))
                    {
                        files.Add(fullPath);
                    }
                }
                else if (Directory.Exists(path))
                {
                    foreach (var file in Directory.EnumerateFiles(
                                 path,
                                 "*",
                                 SearchOption.AllDirectories))
                    {
                        var fullPath = Path.GetFullPath(file);

                        if (seenPaths.Add(fullPath))
                        {
                            files.Add(fullPath);
                        }
                    }
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

        return files;
    }
}

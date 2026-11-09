namespace WifiDirectTransfer.Core;

/// <summary>
/// 解析资源管理器右键菜单和“发送到”传入的路径。
/// 支持 --send path1 path2、--send=path 以及直接传入路径。
/// </summary>
public static class LaunchArguments
{
    private const string SendSwitch = "--send";
    private const string SendSwitchPrefix = "--send=";

    public static IReadOnlyList<string> ParsePaths(
        IReadOnlyList<string>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            if (string.Equals(argument, SendSwitch, StringComparison.OrdinalIgnoreCase))
            {
                index++;

                while (index < arguments.Count)
                {
                    var candidate = arguments[index];

                    if (string.IsNullOrWhiteSpace(candidate) ||
                        candidate.StartsWith("--", StringComparison.Ordinal))
                    {
                        break;
                    }

                    AddPath(paths, seen, candidate);
                    index++;
                }

                index--;
                continue;
            }

            if (argument.StartsWith(
                    SendSwitchPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                AddPath(
                    paths,
                    seen,
                    argument[SendSwitchPrefix.Length..]);
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                // 保留给后续启动参数，当前忽略未知开关。
                continue;
            }

            AddPath(paths, seen, argument);
        }

        return paths;
    }

    private static void AddPath(
        List<string> paths,
        HashSet<string> seen,
        string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        path = path.Trim();

        if (path.Length == 0)
        {
            return;
        }

        // 资源管理器可能给目录参数带上引号；命令行解析通常已经去掉，
        // 这里再兜底一次，避免把引号当成路径的一部分。
        if (path.Length >= 2 &&
            path[0] == '"' &&
            path[^1] == '"')
        {
            path = path[1..^1];
        }

        if (path.Length > 0 && seen.Add(path))
        {
            paths.Add(path);
        }
    }
}

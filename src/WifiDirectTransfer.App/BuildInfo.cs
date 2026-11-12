using System.Reflection;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

/// <summary>应用自身的版本与构建号，供“关于”和托盘提示使用。</summary>
internal static class BuildInfo
{
    private static readonly string Informational = ResolveInformationalVersion();

    public static ReleaseVersion Version { get; } = ResolveVersion(Informational);

    public static string VersionText => Version.ToString();

    /// <summary>提交号短写；没有源码链接信息时为空。</summary>
    public static string Commit { get; } = ResolveCommit(Informational);

    /// <summary>“1.6.2（构建 abc1234）”或仅版本号。</summary>
    public static string DisplayText => Commit.Length == 0
        ? VersionText
        : $"{VersionText}（构建 {Commit}）";

    private static string ResolveInformationalVersion()
    {
        var assembly = typeof(BuildInfo).Assembly;

        return assembly
                   .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "0.0.0";
    }

    private static ReleaseVersion ResolveVersion(string informational)
    {
        if (ReleaseVersion.TryParse(informational, out var version))
        {
            return version;
        }

        var fileVersion = typeof(BuildInfo).Assembly.GetName().Version;

        return fileVersion is null
            ? new ReleaseVersion(0, 0, 0)
            : new ReleaseVersion(
                fileVersion.Major,
                fileVersion.Minor,
                Math.Max(fileVersion.Build, 0));
    }

    private static string ResolveCommit(string informational)
    {
        var separator = informational.IndexOf('+');

        if (separator < 0 || separator == informational.Length - 1)
        {
            return string.Empty;
        }

        var commit = informational[(separator + 1)..].Trim();
        return commit.Length > 7 ? commit[..7] : commit;
    }
}

using System.IO;
using Microsoft.Win32;

namespace WifiDirectTransfer.App;

/// <summary>
/// 判断当前进程是否来自 Inno Setup 安装目录。便携版不提供
/// 自动更新和开机自启，避免写入注册表或覆盖别的副本。
/// </summary>
internal static class InstallationInfo
{
    /// <summary>必须与 installer\WifiDirectTransfer.iss 中的 AppId 保持一致。</summary>
    public const string InstallerAppId =
        "{8F3C2A1E-5B7D-4E9A-9C41-2D6B8F0A7E13}";

    public static bool IsInstalledCopy => CheckInstalledCopy();

    private static bool CheckInstalledCopy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" +
                InstallerAppId + "_is1");

            if (key?.GetValue("InstallLocation") is not string location ||
                string.IsNullOrWhiteSpace(location))
            {
                return false;
            }

            var expected = Path
                .GetFullPath(location)
                .TrimEnd(Path.DirectorySeparatorChar);
            var actual = Path
                .GetFullPath(AppContext.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar);

            return string.Equals(
                       expected,
                       actual,
                       StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(
                       Path.Combine(expected, "WifiDirectTransfer.App.exe"));
        }
        catch (Exception)
        {
            return false;
        }
    }
}

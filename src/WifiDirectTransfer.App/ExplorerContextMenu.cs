using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WifiDirectTransfer.App;

/// <summary>
/// 只在 HKCU 下注册资源管理器右键菜单，不修改系统级或机器级设置。
/// 注册后发送文件和文件夹时会把路径通过 --send 传给应用。
/// </summary>
internal static class ExplorerContextMenu
{
    private const string VerbKeyName = "WifiDirectTransfer.Send";
    private const string DisplayName = "通过 Wi-Fi Direct 发送";

    private const string FileShellRoot = @"Software\Classes\*\shell";
    private const string DirectoryShellRoot = @"Software\Classes\Directory\shell";
    private const string DirectoryBackgroundShellRoot =
        @"Software\Classes\Directory\Background\shell";

    private const uint ShcneAssocChanged = 0x08000000;

    public static bool IsRegistered()
    {
        try
        {
            using var fileKey = Registry.CurrentUser.OpenSubKey(
                $@"{FileShellRoot}\{VerbKeyName}\command");
            return fileKey is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool IsRegisteredFor(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            using var fileKey = Registry.CurrentUser.OpenSubKey(
                $@"{FileShellRoot}\{VerbKeyName}\command");
            var command = fileKey?.GetValue(null) as string;
            return command?.Contains(
                $"\"{Path.GetFullPath(executablePath)}\"",
                StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool Register(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(executablePath);

            CreateVerb(
                FileShellRoot,
                $"\"{fullPath}\" --send \"%1\"",
                fullPath);

            CreateVerb(
                DirectoryShellRoot,
                $"\"{fullPath}\" --send \"%1\"",
                fullPath);

            CreateVerb(
                DirectoryBackgroundShellRoot,
                $"\"{fullPath}\" --send \"%V\"",
                fullPath);

            RefreshShell();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool Unregister()
    {
        try
        {
            DeleteVerb(FileShellRoot);
            DeleteVerb(DirectoryShellRoot);
            DeleteVerb(DirectoryBackgroundShellRoot);
            RefreshShell();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void CreateVerb(
        string shellRoot,
        string command,
        string iconPath)
    {
        using var verbKey = Registry.CurrentUser.CreateSubKey(
            $@"{shellRoot}\{VerbKeyName}");
        verbKey.SetValue(null, DisplayName);
        verbKey.SetValue("Icon", $"\"{iconPath}\",0");
        verbKey.SetValue("MultiSelectModel", "Player");

        using var commandKey = verbKey.CreateSubKey("command");
        commandKey.SetValue(null, command);
    }

    private static void DeleteVerb(string shellRoot)
    {
        Registry.CurrentUser.DeleteSubKeyTree(
            $@"{shellRoot}\{VerbKeyName}",
            throwOnMissingSubKey: false);
    }

    private static void RefreshShell() =>
        SHChangeNotify(
            ShcneAssocChanged,
            0,
            IntPtr.Zero,
            IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        IntPtr item1,
        IntPtr item2);
}

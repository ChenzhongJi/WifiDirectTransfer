using System.IO;
using Microsoft.Win32;

namespace WifiDirectTransfer.App;

/// <summary>
/// 开机自启：只负责把 exe 写进当前用户的 Run 项，命令行带 --tray。
/// 注册表值就是唯一事实来源，不额外写入 settings.json。
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName = "WifiDirectTransfer";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is string value &&
                       !string.IsNullOrWhiteSpace(value);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static bool Enable(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !File.Exists(executablePath))
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                RunKeyPath,
                writable: true);

            if (key is null)
            {
                return false;
            }

            key.SetValue(
                ValueName,
                BuildCommand(executablePath),
                RegistryValueKind.String);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                RunKeyPath,
                writable: true);

            if (key?.GetValue(ValueName) is null)
            {
                return true;
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 生成 Run 项命令行。路径含空格或中文时必须带引号。
    /// </summary>
    internal static string BuildCommand(string executablePath) =>
        $"\"{Path.GetFullPath(executablePath)}\" --tray";
}

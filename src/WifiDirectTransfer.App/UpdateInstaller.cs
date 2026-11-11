using System.Diagnostics;
using System.IO;

namespace WifiDirectTransfer.App;

/// <summary>
/// 启动 Inno Setup 安装包。应用必须先退出，安装器才能替换文件。
/// </summary>
internal static class UpdateInstaller
{
    // /SILENT 显示进度但不询问；安装完成后 Inno 的 [Run] 会重新启动应用。
    private const string InstallerArguments =
        "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS";

    public static void Launch(string setupPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setupPath);

        if (!File.Exists(setupPath))
        {
            throw new FileNotFoundException("更新包不存在。", setupPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = setupPath,
            Arguments = InstallerArguments,
            UseShellExecute = true,
            WorkingDirectory =
                Path.GetDirectoryName(setupPath) ??
                Environment.CurrentDirectory
        };

        if (Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException("无法启动更新安装程序。");
        }
    }
}

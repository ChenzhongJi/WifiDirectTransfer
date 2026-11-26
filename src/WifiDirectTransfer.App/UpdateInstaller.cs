using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace WifiDirectTransfer.App;

/// <summary>
/// 启动 Inno Setup 安装包。应用必须先退出，安装器才能替换文件。
/// </summary>
internal static class UpdateInstaller
{
    // /SILENT 显示进度但不询问；安装器的 [Run] 条目不带 postinstall 标志，
    // 安装结束后（含静默安装）会自动以安装用户身份重新启动应用。
    private const string InstallerArguments =
        "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS";

    public static void Launch(
        string setupPath,
        long expectedSize,
        string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setupPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);

        if (!File.Exists(setupPath))
        {
            throw new FileNotFoundException("更新包不存在。", setupPath);
        }

        // 下载校验与用户确认安装之间存在时间窗，
        // 启动前必须重新校验大小和 SHA-256，防止更新包被替换后仍被执行。
        var actualSize = new FileInfo(setupPath).Length;

        if (actualSize != expectedSize)
        {
            AppLog.Write(
                "更新安装中止：更新包大小不匹配，" +
                $"预期 {expectedSize} 字节，实际 {actualSize} 字节。");
            throw new InvalidDataException(
                $"更新包大小不匹配：预期 {expectedSize} 字节，" +
                $"实际 {actualSize} 字节，已中止安装。");
        }

        string actualHash;

        using (var stream = File.OpenRead(setupPath))
        using (var sha256 = SHA256.Create())
        {
            actualHash = Convert.ToHexString(sha256.ComputeHash(stream));
        }

        if (!string.Equals(
                actualHash,
                expectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Write(
                "更新安装中止：更新包 SHA-256 校验失败，" +
                $"预期 {expectedSha256}，实际 {actualHash}。");
            throw new InvalidDataException(
                "更新包 SHA-256 校验失败，安装可能已被篡改，已中止安装。");
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

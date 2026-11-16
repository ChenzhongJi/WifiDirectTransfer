using System.IO;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

/// <summary>
/// 把主窗口日志同时写入磁盘，便于在无法复制 UI 文本或应用卡死时排查问题。
/// 达到 4 MiB 后轮转为 app.prev.log。
/// </summary>
internal static class AppLog
{
    private const long MaxFileBytes = 4L * 1024 * 1024;

    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WifiDirectTransfer",
        "logs");

    private static readonly RotatingLogWriter Writer = new(
        Path.Combine(LogDirectory, "app.log"),
        MaxFileBytes,
        Path.Combine(LogDirectory, "app.prev.log"));

    public static string FilePath => Writer.FilePath;

    public static void Write(string message)
    {
        try
        {
            Writer.Write($"{DateTimeOffset.Now:O} {message}");
        }
        catch (Exception)
        {
            // Writer 内部已处理 IO 失败；这里兜底，日志不能影响应用运行。
        }
    }
}

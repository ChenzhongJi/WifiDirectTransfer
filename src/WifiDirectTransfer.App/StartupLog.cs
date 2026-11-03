using System.IO;

namespace WifiDirectTransfer.App;

internal static class StartupLog
{
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WifiDirectTransfer",
        "logs",
        "startup.log");

    public static void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:O} {message}";

        try
        {
            Console.WriteLine(line);
        }
        catch (IOException)
        {
            // 日志输出失败不应阻止应用启动。
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
            File.AppendAllText(LogFilePath, line + Environment.NewLine);
        }
        catch (IOException)
        {
            // 日志输出失败不应阻止应用启动。
        }
        catch (UnauthorizedAccessException)
        {
            // 日志输出失败不应阻止应用启动。
        }
    }
}

using System.Diagnostics;
using System.IO;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.Windows;

internal static class WifiDirectErrorLog
{
    private static readonly object Gate = new();
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WifiDirectTransfer",
        "logs",
        "wifi-direct-errors.log");

    public static void WriteUnknown(
        Exception exception,
        WifiDirectOperation operation,
        WifiDirectErrorContext context)
    {
        var message = string.Join(
            Environment.NewLine,
            $"{DateTimeOffset.Now:O} 未知 Wi-Fi Direct 错误",
            $"Operation={operation}, Context={context}, HResult=0x{exception.HResult:X8}",
            exception.ToString(),
            string.Empty);

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
                File.AppendAllText(LogFilePath, message);
            }
        }
        catch (Exception)
        {
            // 日志写入失败不应覆盖原始异常。
        }

        Trace.TraceError(message);
    }
}

using System.IO;
using System.Text;

namespace WifiDirectTransfer.App;

/// <summary>
/// 把主窗口日志同时写入磁盘，便于在无法复制 UI 文本或应用卡死时排查问题。
/// </summary>
internal static class AppLog
{
    private const long MaxFileBytes = 4L * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WifiDirectTransfer",
        "logs");

    private static StreamWriter? _writer;
    private static long _writtenBytes;

    public static string FilePath => Path.Combine(LogDirectory, "app.log");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                var writer = _writer ?? OpenWriter();

                if (writer is null)
                {
                    return;
                }

                var line = $"{DateTimeOffset.Now:O} {message}";
                writer.WriteLine(line);
                _writtenBytes +=
                    Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;

                if (_writtenBytes >= MaxFileBytes)
                {
                    CloseWriter();
                    Rotate();
                }
            }
            catch (Exception)
            {
                // 日志写入失败不能影响应用运行。
                CloseWriter();
            }
        }
    }

    private static StreamWriter? OpenWriter()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var stream = new FileStream(
                FilePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            _writtenBytes = stream.Length;

            return new StreamWriter(stream, new UTF8Encoding(false))
            {
                AutoFlush = true
            };
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Rotate()
    {
        try
        {
            var previousPath = Path.Combine(LogDirectory, "app.prev.log");

            if (File.Exists(previousPath))
            {
                File.Delete(previousPath);
            }

            if (File.Exists(FilePath))
            {
                File.Move(FilePath, previousPath);
            }
        }
        catch (IOException)
        {
            // 轮转失败时继续写当前文件。
        }
        catch (UnauthorizedAccessException)
        {
            // 轮转失败时继续写当前文件。
        }
    }

    private static void CloseWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (IOException)
        {
            // 忽略关闭时的 IO 错误。
        }

        _writer = null;
        _writtenBytes = 0;
    }
}

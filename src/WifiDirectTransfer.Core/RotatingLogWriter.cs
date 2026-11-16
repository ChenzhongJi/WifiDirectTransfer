using System.Text;

namespace WifiDirectTransfer.Core;

/// <summary>
/// 简单的轮转文本日志写入器：超过大小上限时关闭当前文件，把当前文件移动到
/// 前一份日志位置，再在下次写入时重新创建。写入失败一律静默忽略，日志不能
/// 影响主流程。
/// </summary>
public sealed class RotatingLogWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly string _previousFilePath;
    private readonly long _maxFileBytes;

    private StreamWriter? _writer;
    private long _writtenBytes;

    public RotatingLogWriter(
        string filePath,
        long maxFileBytes,
        string? previousFilePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (maxFileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFileBytes),
                "日志文件大小上限必须大于零。");
        }

        _filePath = Path.GetFullPath(filePath);
        _previousFilePath = string.IsNullOrWhiteSpace(previousFilePath)
            ? _filePath + ".prev"
            : Path.GetFullPath(previousFilePath);
        _maxFileBytes = maxFileBytes;
    }

    public string FilePath => _filePath;

    public string PreviousFilePath => _previousFilePath;

    /// <summary>当前是否持有打开的日志文件，供诊断和测试判断 writer 是否被复用。</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _writer is not null;
            }
        }
    }

    public void Write(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_gate)
        {
            try
            {
                var writer = _writer ?? OpenWriter();

                if (writer is null)
                {
                    return;
                }

                writer.WriteLine(message);
                _writtenBytes +=
                    Encoding.UTF8.GetByteCount(message) +
                    Environment.NewLine.Length;

                if (_writtenBytes >= _maxFileBytes)
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

    public void Dispose()
    {
        lock (_gate)
        {
            CloseWriter();
        }
    }

    private StreamWriter? OpenWriter()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = new FileStream(
                _filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            _writtenBytes = stream.Length;
            _writer = new StreamWriter(stream, new UTF8Encoding(false))
            {
                AutoFlush = true
            };

            return _writer;
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

    private void Rotate()
    {
        try
        {
            if (File.Exists(_previousFilePath))
            {
                File.Delete(_previousFilePath);
            }

            if (File.Exists(_filePath))
            {
                File.Move(_filePath, _previousFilePath);
            }
        }
        catch (IOException)
        {
            // 轮转失败时继续使用当前文件，下次写入会再次尝试。
        }
        catch (UnauthorizedAccessException)
        {
            // 轮转失败时继续使用当前文件，下次写入会再次尝试。
        }
        finally
        {
            _writtenBytes = 0;
        }
    }

    private void CloseWriter()
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

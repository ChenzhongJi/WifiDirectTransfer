using System.Globalization;

namespace WifiDirectTransfer.Core;

/// <summary>
/// 文件传输领域异常基类，便于统一映射为用户可读提示。
/// </summary>
public class FileTransferException : WifiDirectException
{
    protected FileTransferException(
        WifiDirectErrorCode errorCode,
        string message,
        Exception? innerException = null)
        : base(errorCode, message, innerException)
    {
    }
}

public sealed class FileTooLargeException : FileTransferException
{
    public FileTooLargeException(long actualSize, long maxSize)
        : base(
            WifiDirectErrorCode.FileTooLarge,
            $"文件大小 {actualSize} 字节超过允许的最大值 {maxSize} 字节。")
    {
        ActualSize = actualSize;
        MaxSize = maxSize;
    }

    public long ActualSize { get; }

    public long MaxSize { get; }
}

public sealed class FileChangedException : FileTransferException
{
    public FileChangedException(long expectedLength, long actualLength)
        : base(
            WifiDirectErrorCode.TransferInterrupted,
            $"文件在传输过程中发生变化：预期 {expectedLength} 字节，实际读取 {actualLength} 字节。")
    {
        ExpectedLength = expectedLength;
        ActualLength = actualLength;
    }

    public long ExpectedLength { get; }

    public long ActualLength { get; }
}

public sealed class TransferTruncatedException : FileTransferException
{
    public TransferTruncatedException(
        string message = "传输被截断：在收到完整文件前连接已结束。",
        Exception? innerException = null)
        : base(WifiDirectErrorCode.TransferInterrupted, message, innerException)
    {
    }
}

public sealed class ChecksumMismatchException : FileTransferException
{
    public ChecksumMismatchException(
        string message = "文件校验失败：SHA-256 摘要不匹配。",
        Exception? innerException = null)
        : base(WifiDirectErrorCode.ChecksumMismatch, message, innerException)
    {
    }
}

public sealed class InvalidFileHeaderException : FileTransferException
{
    public InvalidFileHeaderException(
        string message,
        Exception? innerException = null)
        : base(WifiDirectErrorCode.InvalidProtocolData, message, innerException)
    {
    }
}

public sealed class MessageProtocolException : FileTransferException
{
    public MessageProtocolException(
        string message,
        Exception? innerException = null)
        : base(WifiDirectErrorCode.InvalidProtocolData, message, innerException)
    {
    }
}

public sealed class InsufficientStorageException : FileTransferException
{
    public InsufficientStorageException(long requiredBytes, long availableBytes)
        : base(
            WifiDirectErrorCode.StorageInsufficient,
            BuildMessage(requiredBytes, availableBytes))
    {
        RequiredBytes = Math.Max(0, requiredBytes);
        AvailableBytes = Math.Max(0, availableBytes);
    }

    public long RequiredBytes { get; }

    public long AvailableBytes { get; }

    public long ShortfallBytes => Math.Max(0, RequiredBytes - AvailableBytes);

    public string UserMessage =>
        "接收失败：磁盘空间不足，" +
        $"需要 {FormatMegabytes(RequiredBytes)} MB，" +
        $"可用 {FormatMegabytes(AvailableBytes)} MB";

    private static string BuildMessage(long requiredBytes, long availableBytes)
    {
        var required = Math.Max(0, requiredBytes);
        var available = Math.Max(0, availableBytes);
        var shortfall = Math.Max(0, required - available);

        return
            "磁盘空间不足：" +
            $"需要 {FormatMegabytes(required)} MB，" +
            $"可用 {FormatMegabytes(available)} MB，" +
            $"差 {FormatMegabytes(shortfall)} MB。";
    }

    private static string FormatMegabytes(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("0.##", CultureInfo.InvariantCulture);
}

public sealed class RemoteTransferException : FileTransferException
{
    public RemoteTransferException(
        WifiDirectErrorCode errorCode,
        string message,
        Exception? innerException = null)
        : base(
            errorCode,
            string.IsNullOrWhiteSpace(message) ? "接收端拒绝了传输。" : message,
            innerException)
    {
    }
}

public sealed class FileSkippedException : FileTransferException
{
    public FileSkippedException(string message = "接收端已跳过同名文件。")
        : base(WifiDirectErrorCode.FileSkipped, message)
    {
    }
}

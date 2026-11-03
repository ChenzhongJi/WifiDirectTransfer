namespace WifiDirectTransfer.Core;

public sealed record FileTransferProgress(long BytesTransferred, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0
        ? 1d
        : Math.Clamp((double)BytesTransferred / TotalBytes, 0d, 1d);
}

public sealed record FileSendResult(
    string FileName,
    long FileSize,
    byte[] Sha256,
    long StartOffset);

public sealed record FileTransferStartInfo(
    string FileName,
    long TotalBytes,
    long StartOffset)
{
    public bool IsResume => StartOffset > 0;
}

public sealed class FileStartedEventArgs : EventArgs
{
    public FileStartedEventArgs(FileTransferStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        StartInfo = startInfo;
    }

    public FileTransferStartInfo StartInfo { get; }
}

public sealed record FileReceiveResult(
    string FileName,
    string FilePath,
    long FileSize,
    byte[] Sha256);

public sealed class FileReceivedEventArgs : EventArgs
{
    public FileReceivedEventArgs(FileReceiveResult result, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(result);
        Result = result;
        Elapsed = elapsed;
    }

    public FileReceiveResult Result { get; }

    public TimeSpan Elapsed { get; }
}

public sealed class FileFailedEventArgs : EventArgs
{
    public FileFailedEventArgs(Exception error, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
        FileName = fileName;
    }

    public Exception Error { get; }

    public string? FileName { get; }
}

public sealed class FileSkippedEventArgs : EventArgs
{
    public FileSkippedEventArgs(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        FileName = fileName;
    }

    public string FileName { get; }
}

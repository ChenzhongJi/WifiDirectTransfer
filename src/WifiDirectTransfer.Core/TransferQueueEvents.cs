namespace WifiDirectTransfer.Core;

public sealed class QueueFileStartedEventArgs : EventArgs
{
    public QueueFileStartedEventArgs(PendingFile file, FileTransferStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(startInfo);

        File = file;
        StartInfo = startInfo;
    }

    public PendingFile File { get; }

    public FileTransferStartInfo StartInfo { get; }
}

public sealed class QueueFileCompletedEventArgs : EventArgs
{
    public QueueFileCompletedEventArgs(PendingFile file, FileSendResult result)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(result);

        File = file;
        Result = result;
    }

    public PendingFile File { get; }

    public FileSendResult Result { get; }
}

public sealed class QueueFileFailedEventArgs : EventArgs
{
    public QueueFileFailedEventArgs(PendingFile file, Exception error)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(error);

        File = file;
        Error = error;
    }

    public PendingFile File { get; }

    public Exception Error { get; }
}

public sealed class QueueFileSkippedEventArgs : EventArgs
{
    public QueueFileSkippedEventArgs(PendingFile file, string reason)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(reason);

        File = file;
        Reason = reason;
    }

    public PendingFile File { get; }

    public string Reason { get; }
}

public sealed class TransferQueueCompletedEventArgs : EventArgs
{
    public TransferQueueCompletedEventArgs(
        int totalCount,
        int completedCount,
        int failedCount,
        int skippedCount,
        bool wasCanceled)
    {
        TotalCount = totalCount;
        CompletedCount = completedCount;
        FailedCount = failedCount;
        SkippedCount = skippedCount;
        WasCanceled = wasCanceled;
    }

    public int TotalCount { get; }

    public int CompletedCount { get; }

    public int FailedCount { get; }

    public int SkippedCount { get; }

    public bool WasCanceled { get; }
}

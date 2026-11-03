using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

internal sealed class ReceivedFileItem
{
    public ReceivedFileItem(FileReceiveResult result, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(result);

        Result = result;
        ReceivedAt = receivedAt;
    }

    public FileReceiveResult Result { get; }

    public DateTimeOffset ReceivedAt { get; }

    public string FileName => Result.FileName;

    public string FilePath => Result.FilePath;

    public long FileSize => Result.FileSize;

    public string DetailText =>
        $"{FileSize:N0} 字节 · {ReceivedAt:HH:mm:ss}";
}

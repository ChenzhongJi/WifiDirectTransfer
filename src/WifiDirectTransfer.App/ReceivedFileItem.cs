using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

internal sealed class ReceivedFileItem : INotifyPropertyChanged
{
    private bool _isMissing;

    public ReceivedFileItem(FileReceiveResult result, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(result);

        Result = result;
        ReceivedAt = receivedAt;
    }

    public FileReceiveResult Result { get; }

    public DateTimeOffset ReceivedAt { get; }

    public string FileName => Result.FileName;

    public string DisplayPath => Result.DisplayPath;

    public string FilePath => Result.FilePath;

    public long FileSize => Result.FileSize;

    public bool IsMissing
    {
        get => _isMissing;
        private set
        {
            if (_isMissing == value)
            {
                return;
            }

            _isMissing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailText));
        }
    }

    public string DetailText =>
        $"{FileSize:N0} 字节 · {ReceivedAt:HH:mm:ss}" +
        (IsMissing ? " · 文件已不存在" : string.Empty);

    /// <summary>
    /// 重新检查接收文件是否仍在磁盘上。
    /// </summary>
    public bool RefreshAvailability()
    {
        var exists = File.Exists(FilePath);
        IsMissing = !exists;
        return exists;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
}

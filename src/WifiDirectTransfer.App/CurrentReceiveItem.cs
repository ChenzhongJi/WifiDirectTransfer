using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

internal sealed class CurrentReceiveItem : INotifyPropertyChanged
{
    private readonly TransferRateTracker _rateTracker = new();
    private readonly Stopwatch _transferWatch = new();

    private long _bytesTransferred;

    public CurrentReceiveItem(FileTransferStartInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        FileName = info.FileName;
        DisplayPath = info.DisplayPath;
        FileSize = info.TotalBytes;
        StartOffset = info.StartOffset;
        IsResume = info.IsResume;
        _bytesTransferred = info.StartOffset;
        _rateTracker.Reset(info.StartOffset);
        _transferWatch.Start();
    }

    public string FileName { get; }

    public string DisplayPath { get; }

    public long FileSize { get; }

    public long StartOffset { get; }

    public bool IsResume { get; }

    public long BytesTransferred
    {
        get => _bytesTransferred;
        set
        {
            if (_bytesTransferred == value)
            {
                return;
            }

            _bytesTransferred = value;
            _rateTracker.Update(value, _transferWatch.Elapsed);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(SpeedText));
            OnPropertyChanged(nameof(EtaText));
            OnPropertyChanged(nameof(RateText));
        }
    }

    public double ProgressPercent => FileSize <= 0
        ? 100d
        : Math.Clamp(
            (double)BytesTransferred / FileSize * 100d,
            0d,
            100d);

    public string SpeedText =>
        TransferFormat.FormatSpeed(_rateTracker.BytesPerSecond);

    public string EtaText
    {
        get
        {
            var remaining = _rateTracker.GetRemaining(FileSize);
            return remaining.HasValue
                ? TransferFormat.FormatDuration(remaining.Value)
                : "—";
        }
    }

    public string RateText => _rateTracker.HasValue
        ? $"{SpeedText} · 剩余 {EtaText}"
        : "正在估算速度…";

    public string DetailText
    {
        get
        {
            var bytes = IsResume
                ? $"{FormatBytes(BytesTransferred)} / {FormatBytes(FileSize)} · " +
                  $"从 {FormatBytes(StartOffset)} 续传"
                : $"{FormatBytes(BytesTransferred)} / {FormatBytes(FileSize)}";

            return $"{bytes} · {RateText}";
        }
    }

    public void UpdateProgress(long bytesTransferred)
    {
        BytesTransferred = bytesTransferred;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} 字节",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.0} GB"
    };
}

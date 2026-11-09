using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace WifiDirectTransfer.Core;

public enum PendingFileStatus
{
    Waiting,
    Transferring,
    Completed,
    Failed,
    Skipped
}

public sealed class PendingFile : INotifyPropertyChanged
{
    private readonly TransferRateTracker _rateTracker = new();
    private readonly Stopwatch _transferWatch = new();

    private PendingFileStatus _status;
    private string? _errorMessage;
    private long _bytesTransferred;
    private long _startOffset;

    public PendingFile(
        string sourcePath,
        string fileName,
        long size,
        string relativeDirectory = "")
    {
        SourcePath = sourcePath;
        FileName = fileName;
        Size = size;
        RelativeDirectory = relativeDirectory;
    }

    public string SourcePath { get; }

    public string FileName { get; }

    public string RelativeDirectory { get; }

    public string DisplayPath => string.IsNullOrEmpty(RelativeDirectory)
        ? FileName
        : Path.Combine(RelativeDirectory, FileName);

    public long Size { get; }

    public PendingFileStatus Status
    {
        get => _status;
        internal set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;

            if (value == PendingFileStatus.Transferring)
            {
                _rateTracker.Reset(_bytesTransferred);
                _transferWatch.Restart();
            }
            else
            {
                _transferWatch.Stop();
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(SpeedBytesPerSecond));
            OnPropertyChanged(nameof(SpeedText));
            OnPropertyChanged(nameof(EtaText));
            OnPropertyChanged(nameof(TransferRateText));
        }
    }

    public string StatusText => Status switch
    {
        PendingFileStatus.Waiting => "等待中",
        PendingFileStatus.Transferring => "传输中",
        PendingFileStatus.Completed => "已完成",
        PendingFileStatus.Failed => "失败",
        PendingFileStatus.Skipped => "已跳过",
        _ => Status.ToString()
    };

    public string? ErrorMessage
    {
        get => _errorMessage;
        internal set
        {
            if (_errorMessage == value)
            {
                return;
            }

            _errorMessage = value;
            OnPropertyChanged();
        }
    }

    public long BytesTransferred
    {
        get => _bytesTransferred;
        internal set
        {
            if (_bytesTransferred == value)
            {
                return;
            }

            _bytesTransferred = value;
            _rateTracker.Update(value, _transferWatch.Elapsed);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(SpeedBytesPerSecond));
            OnPropertyChanged(nameof(SpeedText));
            OnPropertyChanged(nameof(EtaText));
            OnPropertyChanged(nameof(TransferRateText));
        }
    }

    public long StartOffset
    {
        get => _startOffset;
        internal set
        {
            if (_startOffset == value)
            {
                return;
            }

            _startOffset = value;
            _rateTracker.Reset(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsResume));
            OnPropertyChanged(nameof(SpeedBytesPerSecond));
            OnPropertyChanged(nameof(SpeedText));
            OnPropertyChanged(nameof(EtaText));
            OnPropertyChanged(nameof(TransferRateText));
        }
    }

    public bool IsResume => StartOffset > 0;

    public double ProgressPercent
    {
        get
        {
            if (Status == PendingFileStatus.Completed)
            {
                return 100d;
            }

            if (Size <= 0)
            {
                return 0d;
            }

            return Math.Clamp(
                (double)BytesTransferred / Size * 100d,
                0d,
                100d);
        }
    }

    public string ProgressText => Status == PendingFileStatus.Waiting
        ? "—"
        : $"{ProgressPercent:0}%";

    /// <summary>当前平滑速率，单位字节/秒；未开始或尚未采样时为 0。</summary>
    public double SpeedBytesPerSecond =>
        Status == PendingFileStatus.Transferring
            ? _rateTracker.BytesPerSecond
            : 0d;

    public string SpeedText => Status == PendingFileStatus.Transferring
        ? TransferFormat.FormatSpeed(_rateTracker.BytesPerSecond)
        : "—";

    public string EtaText
    {
        get
        {
            if (Status != PendingFileStatus.Transferring)
            {
                return "—";
            }

            var remaining = _rateTracker.GetRemaining(Size);
            return remaining.HasValue
                ? TransferFormat.FormatDuration(remaining.Value)
                : "—";
        }
    }

    /// <summary>供队列当前文件摘要显示，例如“12.3 MB/s · 剩余 01:24”。</summary>
    public string TransferRateText
    {
        get
        {
            if (Status != PendingFileStatus.Transferring)
            {
                return "—";
            }

            return _rateTracker.HasValue
                ? $"{SpeedText} · 剩余 {EtaText}"
                : "正在估算速度…";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

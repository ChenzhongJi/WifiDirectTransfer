using System.ComponentModel;
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
    private PendingFileStatus _status;
    private string? _errorMessage;
    private long _bytesTransferred;
    private long _startOffset;

    public PendingFile(string sourcePath, string fileName, long size)
    {
        SourcePath = sourcePath;
        FileName = fileName;
        Size = size;
    }

    public string SourcePath { get; }

    public string FileName { get; }

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
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(ProgressText));
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
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(ProgressText));
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
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsResume));
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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

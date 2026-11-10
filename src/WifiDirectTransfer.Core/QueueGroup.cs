using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WifiDirectTransfer.Core;

/// <summary>
/// 队列中的一个顶层项目：单个文件，或用户选择的一个文件夹。
/// 文件夹组包含组内文件和空文件夹，但 UI 只显示顶层项目。
/// </summary>
public sealed class QueueGroup : INotifyPropertyChanged
{
    private readonly List<PendingFile> _files;
    private bool _emptyDirectoriesSent;

    public QueueGroup(
        Guid id,
        string displayName,
        bool isFolder,
        IEnumerable<PendingFile> files,
        IEnumerable<string> emptyDirectories,
        string? sourcePath = null,
        string? destinationRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(emptyDirectories);

        Id = id;
        DisplayName = displayName;
        IsFolder = isFolder;
        DestinationRoot = destinationRoot;
        _files = files.ToList();
        EmptyDirectories = emptyDirectories.ToArray();
        SourcePath = isFolder && _files.Count == 0
            ? null
            : ResolveSourcePath(
                sourcePath ?? _files.FirstOrDefault()?.SourcePath,
                isFolder,
                _files.FirstOrDefault());

        foreach (var file in _files)
        {
            file.QueueGroupId = id;
            file.PropertyChanged += OnFilePropertyChanged;
        }
    }

    public Guid Id { get; }

    public string DisplayName { get; }

    public bool IsFolder { get; }

    /// <summary>文件夹项目实际发送到的顶层相对目录；直接文件为 null。</summary>
    public string? DestinationRoot { get; }

    public IReadOnlyList<PendingFile> Files => _files;

    public IReadOnlyList<string> EmptyDirectories { get; }

    public int FileCount => _files.Count;

    public int EmptyDirectoryCount => EmptyDirectories.Count;

    public string? SourcePath { get; }

    public string DisplayPath => IsFolder
        ? DisplayName
        : _files.Count > 0
            ? _files[0].DisplayPath
            : DisplayName;

    public string Subtitle
    {
        get
        {
            if (!IsFolder)
            {
                return SourcePath ?? string.Empty;
            }

            if (FileCount == 0)
            {
                return $"{EmptyDirectoryCount} 个空文件夹";
            }

            return EmptyDirectoryCount > 0
                ? $"{FileCount} 个文件 · {EmptyDirectoryCount} 个空文件夹"
                : $"{FileCount} 个文件";
        }
    }

    public long Size => _files.Sum(file => file.Size);

    public long BytesTransferred => _files.Sum(file => file.BytesTransferred);

    public int CompletedCount => _files.Count(
        file => file.Status == PendingFileStatus.Completed);

    public int FailedCount => _files.Count(
        file => file.Status == PendingFileStatus.Failed);

    public int SkippedCount => _files.Count(
        file => file.Status == PendingFileStatus.Skipped);

    public bool HasFailures => FailedCount > 0;

    public string ResultText
    {
        get
        {
            if (FileCount == 0)
            {
                return "—";
            }

            var text = $"完成 {CompletedCount} / {FileCount}";

            if (FailedCount > 0)
            {
                text += $"，失败 {FailedCount}";
            }

            if (SkippedCount > 0)
            {
                text += $"，跳过 {SkippedCount}";
            }

            return text;
        }
    }

    public string? ErrorMessage => _files
        .FirstOrDefault(file => file.Status == PendingFileStatus.Failed)
        ?.ErrorMessage;

    public bool EmptyDirectoriesSent => _emptyDirectoriesSent;

    public double ProgressPercent
    {
        get
        {
            if (_files.Count == 0)
            {
                return _emptyDirectoriesSent ? 100d : 0d;
            }

            if (_files.All(file => file.Status == PendingFileStatus.Completed))
            {
                return 100d;
            }

            var size = Size;

            if (size <= 0)
            {
                return 0d;
            }

            return Math.Clamp(
                (double)BytesTransferred / size * 100d,
                0d,
                100d);
        }
    }

    public string ProgressText
    {
        get
        {
            if (_files.Count == 0)
            {
                return _emptyDirectoriesSent ? "100%" : "—";
            }

            if (_files.All(file => file.Status == PendingFileStatus.Waiting))
            {
                return "—";
            }

            return $"{ProgressPercent:0}%";
        }
    }

    public string StatusText
    {
        get
        {
            if (_files.Count == 0)
            {
                return _emptyDirectoriesSent ? "已创建" : "等待创建";
            }

            if (_files.Any(file => file.Status == PendingFileStatus.Transferring))
            {
                return "传输中";
            }

            if (_files.All(file => file.Status == PendingFileStatus.Completed))
            {
                return "已完成";
            }

            if (_files.Any(file => file.Status == PendingFileStatus.Failed))
            {
                return CompletedCount > 0 ? "部分失败" : "失败";
            }

            if (_files.All(file => file.Status == PendingFileStatus.Skipped))
            {
                return "已跳过";
            }

            if (_files.All(file => file.Status is
                    PendingFileStatus.Completed or
                    PendingFileStatus.Failed or
                    PendingFileStatus.Skipped))
            {
                return SkippedCount > 0 ? "部分跳过" : "已完成";
            }

            return "等待中";
        }
    }

    /// <summary>供队列状态颜色选择使用。</summary>
    public PendingFileStatus Status
    {
        get
        {
            if (_files.Count == 0)
            {
                return _emptyDirectoriesSent
                    ? PendingFileStatus.Completed
                    : PendingFileStatus.Waiting;
            }

            if (_files.Any(file => file.Status == PendingFileStatus.Transferring))
            {
                return PendingFileStatus.Transferring;
            }

            if (_files.All(file => file.Status == PendingFileStatus.Completed))
            {
                return PendingFileStatus.Completed;
            }

            if (_files.Any(file => file.Status == PendingFileStatus.Failed))
            {
                return PendingFileStatus.Failed;
            }

            if (_files.All(file => file.Status == PendingFileStatus.Skipped))
            {
                return PendingFileStatus.Skipped;
            }

            if (_files.All(file => file.Status is
                    PendingFileStatus.Completed or
                    PendingFileStatus.Failed or
                    PendingFileStatus.Skipped))
            {
                return SkippedCount > 0
                    ? PendingFileStatus.Skipped
                    : PendingFileStatus.Completed;
            }

            return PendingFileStatus.Waiting;
        }
    }

    public double SpeedBytesPerSecond => _files.Sum(
        file => file.SpeedBytesPerSecond);

    public string SpeedText => TransferFormat.FormatSpeed(SpeedBytesPerSecond);

    public string EtaText
    {
        get
        {
            if (Size <= 0)
            {
                return "—";
            }

            var remaining = Size - BytesTransferred;
            var speed = SpeedBytesPerSecond;

            if (remaining <= 0)
            {
                return "—";
            }

            if (speed <= 0)
            {
                return "—";
            }

            return TransferFormat.FormatDuration(
                TimeSpan.FromSeconds(remaining / speed));
        }
    }

    public string TransferRateText => SpeedBytesPerSecond > 0
        ? $"{SpeedText} · 剩余 {EtaText}"
        : "—";

    public string StartOffsetText
    {
        get
        {
            if (_files.Count != 1)
            {
                return "—";
            }

            return FormatBytes(_files[0].StartOffset);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void MarkEmptyDirectoriesSent()
    {
        if (_emptyDirectoriesSent)
        {
            return;
        }

        _emptyDirectoriesSent = true;
        RaiseAggregateProperties();
    }

    internal bool RemoveFile(PendingFile file)
    {
        if (!_files.Remove(file))
        {
            return false;
        }

        file.PropertyChanged -= OnFilePropertyChanged;
        RaiseAggregateProperties();
        return true;
    }

    internal bool MoveFile(PendingFile file, int offset)
    {
        var waiting = _files
            .Where(item => item.Status == PendingFileStatus.Waiting)
            .ToList();
        var index = waiting.IndexOf(file);
        var targetIndex = index + offset;

        if (index < 0 ||
            targetIndex < 0 ||
            targetIndex >= waiting.Count)
        {
            return false;
        }

        var target = waiting[targetIndex];
        var sourceIndex = _files.IndexOf(file);
        var targetListIndex = _files.IndexOf(target);

        if (sourceIndex < 0 || targetListIndex < 0)
        {
            return false;
        }

        _files.RemoveAt(sourceIndex);
        _files.Insert(targetListIndex, file);
        RaiseAggregateProperties();
        return true;
    }

    private void OnFilePropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        RaiseAggregateProperties();
    }

    private void RaiseAggregateProperties()
    {
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(BytesTransferred));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(SkippedCount));
        OnPropertyChanged(nameof(HasFailures));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SpeedBytesPerSecond));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
        OnPropertyChanged(nameof(TransferRateText));
        OnPropertyChanged(nameof(StartOffsetText));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(EmptyDirectoriesSent));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} 字节",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.0} GB"
    };

    private static string? ResolveSourcePath(
        string? sourcePath,
        bool isFolder,
        PendingFile? firstFile)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !isFolder ||
            firstFile is null)
        {
            return sourcePath;
        }

        var segments = firstFile.RelativeDirectory
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = sourcePath;

        for (var index = 0; index < segments.Length; index++)
        {
            var parent = Path.GetDirectoryName(current);

            if (string.IsNullOrEmpty(parent))
            {
                return sourcePath;
            }

            current = parent;
        }

        return current;
    }
}

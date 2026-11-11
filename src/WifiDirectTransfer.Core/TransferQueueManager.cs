using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace WifiDirectTransfer.Core;

/// <summary>
/// 发送端多文件串行队列。每个文件单独执行一次 FileSender.SendFileAsync，
/// 因此接收端会针对每个文件独立检查 .part/.meta 并决定是否续传。
/// 队列运行期间只处理一个文件；单个文件失败不会阻塞后续文件。
/// </summary>
public sealed class TransferQueueManager : INotifyPropertyChanged
{
    private readonly MessageDispatcher _dispatcher;
    private readonly long _maxFileSize;
    private readonly bool _supportsDirectoryManifest;
    private readonly bool _supportsWindowedTransfer;
    private readonly Func<Exception, string>? _errorMessageFactory;
    private readonly Action<string>? _log;
    private readonly Queue<PendingFile> _pending = new();
    private readonly Queue<PendingManifestBatch> _pendingManifests = new();
    private readonly ObservableCollection<PendingFile> _files = new();
    private readonly ObservableCollection<QueueGroup> _groups = new();
    private readonly Dictionary<Guid, QueueGroup> _groupsById = new();
    private readonly ReadOnlyObservableCollection<PendingFile> _readOnlyFiles;
    private readonly ReadOnlyObservableCollection<QueueGroup> _readOnlyGroups;
    private readonly object _gate = new();
    private readonly TransferRateTracker _overallRate = new();
    private readonly Stopwatch _overallWatch = new();

    private PendingFile? _currentFile;
    private PendingFile? _selectedFile;
    private QueueGroup? _selectedGroup;
    private bool _isRunning;
    private bool _pauseRequested;
    private TaskCompletionSource? _pauseTcs;
    private int _completedCount;
    private int _failedCount;
    private int _skippedCount;
    private int _emptyDirectoryCount;
    private int _skippedEmptyDirectoryCount;
    private int _sentEmptyDirectoryCount;

    public TransferQueueManager(
        MessageDispatcher dispatcher,
        long maxFileSize = FileTransferProtocol.DefaultMaxFileSize,
        Func<Exception, string>? errorMessageFactory = null,
        Action<string>? log = null,
        bool supportsDirectoryManifest = true,
        bool supportsWindowedTransfer = false)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (maxFileSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFileSize),
                "最大文件大小必须大于零。");
        }

        _dispatcher = dispatcher;
        _maxFileSize = maxFileSize;
        _supportsDirectoryManifest = supportsDirectoryManifest;
        _supportsWindowedTransfer = supportsWindowedTransfer;
        _errorMessageFactory = errorMessageFactory;
        _log = log;
        _readOnlyFiles = new ReadOnlyObservableCollection<PendingFile>(_files);
        _readOnlyGroups = new ReadOnlyObservableCollection<QueueGroup>(_groups);
    }

    /// <summary>
    /// 供 UI 绑定。只能在与 UI 相同的线程上通过 <see cref="EnqueueFiles"/> /
    /// <see cref="Clear"/> 修改。
    /// </summary>
    public ReadOnlyObservableCollection<PendingFile> Files => _readOnlyFiles;

    /// <summary>供 UI 绑定的顶层项目：直接文件或用户选择的文件夹。</summary>
    public ReadOnlyObservableCollection<QueueGroup> Groups => _readOnlyGroups;

    public int GroupCount => _groups.Count;

    public bool HasGroups => _groups.Count > 0;

    public PendingFile? CurrentFile
    {
        get => _currentFile;
        private set
        {
            if (ReferenceEquals(_currentFile, value))
            {
                return;
            }

            _currentFile = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentIndex));
            OnPropertyChanged(nameof(CurrentPositionText));
            OnPropertyChanged(nameof(CurrentGroup));
            OnPropertyChanged(nameof(DetailsFile));
            OnPropertyChanged(nameof(DetailsPositionText));
            OnPropertyChanged(nameof(DetailsGroup));
            OnPropertyChanged(nameof(DetailsGroupPositionText));
        }
    }

    /// <summary>
    /// 用户在队列中选中的文件。未选中时详情回退到当前传输文件。
    /// </summary>
    public PendingFile? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (ReferenceEquals(_selectedFile, value))
            {
                return;
            }

            _selectedFile = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailsFile));
            OnPropertyChanged(nameof(DetailsPositionText));
        }
    }

    public PendingFile? DetailsFile => _selectedFile ?? CurrentFile;

    public QueueGroup? CurrentGroup =>
        _currentFile is null ? null : FindGroup(_currentFile);

    /// <summary>
    /// 用户在队列中选中的顶层项目。未选中时详情回退到当前文件所属分组。
    /// </summary>
    public QueueGroup? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (ReferenceEquals(_selectedGroup, value))
            {
                return;
            }

            _selectedGroup = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailsGroup));
            OnPropertyChanged(nameof(DetailsGroupPositionText));
        }
    }

    public QueueGroup? DetailsGroup => _selectedGroup ?? CurrentGroup;

    public string DetailsGroupPositionText
    {
        get
        {
            var group = DetailsGroup;

            if (group is null)
            {
                return "—";
            }

            var index = _groups.IndexOf(group);
            return index < 0
                ? "—"
                : $"第 {index + 1} / {_groups.Count} 项";
        }
    }

    public QueueGroup? FindGroup(PendingFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return _groupsById.TryGetValue(file.QueueGroupId, out var group)
            ? group
            : null;
    }

    public string DetailsPositionText
    {
        get
        {
            var details = DetailsFile;

            if (details is null)
            {
                return "—";
            }

            var index = _files.IndexOf(details);
            return index < 0
                ? "—"
                : $"第 {index + 1} / {_files.Count} 个文件";
        }
    }

    /// <summary>
    /// 当前文件在队列中的 1 基位置；尚未开始或队列为空时为 0。
    /// </summary>
    public int CurrentIndex => _currentFile is null
        ? 0
        : _files.IndexOf(_currentFile) + 1;

    public int TotalCount => _files.Count;

    public int WaitingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public int CompletedCount => _completedCount;

    public int FailedCount => _failedCount;

    public int SkippedCount => _skippedCount;

    public int EmptyDirectoryCount
    {
        get
        {
            lock (_gate)
            {
                return _emptyDirectoryCount;
            }
        }
    }

    public int SkippedEmptyDirectoryCount
    {
        get
        {
            lock (_gate)
            {
                return _skippedEmptyDirectoryCount;
            }
        }
    }

    public int FinishedCount => _completedCount + _failedCount + _skippedCount;

    public bool HasEmptyDirectories => EmptyDirectoryCount > 0;

    public bool ShowEmptyDirectorySummary => HasFiles && HasEmptyDirectories;

    public string EmptyDirectorySummary => EmptyDirectoryCount == 0
        ? string.Empty
        : ShowEmptyDirectorySummary
            ? $"另含 {EmptyDirectoryCount} 个空文件夹"
            : $"将创建 {EmptyDirectoryCount} 个空文件夹";

    public bool SupportsDirectoryManifest => _supportsDirectoryManifest;

    public bool SupportsWindowedTransfer => _supportsWindowedTransfer;

    /// <summary>尚未发送的空文件夹清单，用于自动重连。</summary>
    public IReadOnlyList<string> PendingEmptyDirectories
    {
        get
        {
            lock (_gate)
            {
                return _groups
                    .Where(group => !group.EmptyDirectoriesSent)
                    .SelectMany(group => group.EmptyDirectories)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _isRunning;
            }
        }
    }

    /// <summary>
    /// 是否已请求暂停。当前文件会继续完成，随后队列停在文件边界。
    /// </summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _pauseRequested;
            }
        }
    }

    public bool CanPause => IsRunning && !IsPaused;

    public bool CanResume => IsPaused;

    public bool HasFiles => _files.Count > 0;

    public bool HasWaitingFiles
    {
        get
        {
            lock (_gate)
            {
                return _isRunning || HasPendingWorkLocked();
            }
        }
    }

    public bool CanStart
    {
        get
        {
            lock (_gate)
            {
                return !_isRunning && HasPendingWorkLocked();
            }
        }
    }

    private bool HasPendingWorkLocked()
    {
        return _pending.Count > 0 ||
               _groups.Any(group =>
                   group.EmptyDirectoryCount > 0 &&
                   !group.EmptyDirectoriesSent);
    }

    public string CurrentPositionText => TotalCount == 0
        ? "第 0 / 0 个文件"
        : $"第 {CurrentIndex} / {TotalCount} 个文件";

    public string OverallProgressText
    {
        get
        {
            if (TotalCount == 0)
            {
                if (_emptyDirectoryCount == 0)
                {
                    return "尚未添加文件";
                }

                return _sentEmptyDirectoryCount >= _emptyDirectoryCount
                    ? $"已创建 {_emptyDirectoryCount} 个空文件夹"
                    : $"将创建 {_emptyDirectoryCount} 个空文件夹";
            }

            var text = $"已完成 {CompletedCount} / {TotalCount}";

            if (FailedCount > 0)
            {
                text += $"，失败 {FailedCount}";
            }

            if (SkippedCount > 0)
            {
                text += $"，跳过 {SkippedCount}";
            }

            if (_emptyDirectoryCount > 0)
            {
                text += $"，空文件夹 {_emptyDirectoryCount}";
            }

            return text;
        }
    }

    /// <summary>
    /// 总体进度按已处理（完成、失败、跳过）文件数计算，失败文件不会卡住进度条。
    /// 没有文件时按已发送的空文件夹清单计算。
    /// </summary>
    public double OverallProgressPercent
    {
        get
        {
            if (TotalCount == 0)
            {
                return _emptyDirectoryCount == 0
                    ? 0d
                    : Math.Clamp(
                        (double)_sentEmptyDirectoryCount /
                        _emptyDirectoryCount * 100d,
                        0d,
                        100d);
            }

            return Math.Clamp(
                (double)FinishedCount / TotalCount * 100d,
                0d,
                100d);
        }
    }

    /// <summary>队列中所有文件的字节总数。</summary>
    public long TotalBytes
    {
        get
        {
            long total = 0;

            foreach (var file in _files)
            {
                total += file.Size;
            }

            return total;
        }
    }

    /// <summary>队列总体平滑速率，单位字节/秒。</summary>
    public double OverallSpeedBytesPerSecond => IsRunning
        ? _overallRate.BytesPerSecond
        : 0d;

    public string OverallSpeedText => IsRunning
        ? TransferFormat.FormatSpeed(_overallRate.BytesPerSecond)
        : "—";

    public string OverallEtaText
    {
        get
        {
            if (!IsRunning)
            {
                return "—";
            }

            var remaining = _overallRate.GetRemaining(TotalBytes);
            return remaining.HasValue
                ? TransferFormat.FormatDuration(remaining.Value)
                : "—";
        }
    }

    /// <summary>供总体进度条下方显示，例如“12.3 MB/s · 剩余 01:24”。</summary>
    public string OverallTransferText
    {
        get
        {
            if (!IsRunning)
            {
                return "—";
            }

            return _overallRate.HasValue
                ? $"{OverallSpeedText} · 剩余 {OverallEtaText}"
                : "正在估算速度…";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<QueueFileStartedEventArgs>? FileStarted;

    public event EventHandler<QueueFileCompletedEventArgs>? FileCompleted;

    public event EventHandler<QueueFileFailedEventArgs>? FileFailed;

    public event EventHandler<QueueFileSkippedEventArgs>? FileSkipped;

    public event EventHandler<TransferQueueCompletedEventArgs>? QueueCompleted;

    /// <summary>
    /// 将文件加入队尾。每个文件是一个顶层项目。
    /// 路径不存在时仍会入队，并在发送时标记为失败，
    /// 这样单个无效路径不会阻止其他文件。
    /// </summary>
    public IReadOnlyList<PendingFile> EnqueueFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var sources = new List<TransferSource>();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            sources.Add(CreateSourceFromPath(path));
        }

        return EnqueuePlan(new TransferPlan(sources, Array.Empty<string>())).Files;
    }

    /// <summary>
    /// 将带相对目录的源文件加入队尾。相对目录为空表示发送到接收目录根。
    /// </summary>
    public IReadOnlyList<PendingFile> EnqueueItems(
        IEnumerable<TransferSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var validSources = sources
            .Where(source => source is not null &&
                             !string.IsNullOrWhiteSpace(source.SourcePath))
            .ToArray();

        return EnqueuePlan(
                new TransferPlan(validSources, Array.Empty<string>()))
            .Files;
    }

    /// <summary>
    /// 将文件和空文件夹清单加入队尾。每个顶层项目形成一行队列。
    /// </summary>
    public QueueEnqueueResult EnqueuePlan(TransferPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var groupPlans = plan.Groups.Count > 0
            ? plan.Groups
            : new TransferPlan(plan.Files, plan.EmptyDirectories).Groups;
        var addedFiles = new List<PendingFile>();
        var addedGroups = new List<QueueGroup>();
        var queuedEmptyDirectories = 0;
        var skippedEmptyDirectories = 0;

        lock (_gate)
        {
            foreach (var groupPlan in groupPlans)
            {
                var requestedRoot = groupPlan.IsFolder
                    ? GetRootRelativePath(groupPlan)
                    : string.Empty;
                var destinationRoot = groupPlan.IsFolder &&
                                      requestedRoot.Length > 0
                    ? GetAvailableDestinationRootLocked(requestedRoot)
                    : null;
                var groupFiles = new List<PendingFile>();

                foreach (var source in groupPlan.Files)
                {
                    if (source is null ||
                        string.IsNullOrWhiteSpace(source.SourcePath))
                    {
                        continue;
                    }

                    var adjustedSource = destinationRoot is null
                        ? source
                        : source with
                        {
                            RelativeDirectory = ReplaceRoot(
                                source.RelativeDirectory,
                                requestedRoot,
                                destinationRoot)
                        };
                    groupFiles.Add(CreatePendingFile(adjustedSource));
                }

                var emptyDirectories = groupPlan.EmptyDirectories
                    .Select(directory => destinationRoot is null
                        ? directory
                        : ReplaceRoot(
                            directory,
                            requestedRoot,
                            destinationRoot))
                    .Where(directory => !string.IsNullOrWhiteSpace(directory))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var groupEmptyDirectories = Array.Empty<string>();
                var skipped = 0;

                if (emptyDirectories.Length > 0)
                {
                    if (_supportsDirectoryManifest)
                    {
                        groupEmptyDirectories = emptyDirectories;
                    }
                    else
                    {
                        skipped = emptyDirectories.Length;
                        _skippedEmptyDirectoryCount += skipped;
                    }
                }

                skippedEmptyDirectories += skipped;

                if (groupFiles.Count == 0 &&
                    groupEmptyDirectories.Length == 0 &&
                    skipped == 0)
                {
                    continue;
                }

                var displayBase = destinationRoot ?? groupPlan.DisplayName;
                var displayName = GetUniqueGroupNameLocked(displayBase);
                var group = new QueueGroup(
                    Guid.NewGuid(),
                    displayName,
                    groupPlan.IsFolder,
                    groupFiles,
                    groupEmptyDirectories,
                    groupPlan.SourcePath,
                    destinationRoot);

                _groups.Add(group);
                _groupsById.Add(group.Id, group);
                addedGroups.Add(group);
                addedFiles.AddRange(groupFiles);

                foreach (var file in groupFiles)
                {
                    _files.Add(file);
                    _pending.Enqueue(file);
                }

                if (groupEmptyDirectories.Length > 0)
                {
                    _pendingManifests.Enqueue(
                        new PendingManifestBatch(
                            group.Id,
                            groupEmptyDirectories));
                    _emptyDirectoryCount += groupEmptyDirectories.Length;
                    queuedEmptyDirectories += groupEmptyDirectories.Length;
                }
            }
        }

        if (addedFiles.Count == 0 &&
            queuedEmptyDirectories == 0 &&
            skippedEmptyDirectories == 0)
        {
            return new QueueEnqueueResult(
                addedFiles,
                queuedEmptyDirectories,
                skippedEmptyDirectories,
                addedGroups);
        }

        if (skippedEmptyDirectories > 0)
        {
            _log?.Invoke(
                $"对端版本低于 1.3，已跳过 {skippedEmptyDirectories} 个空文件夹。");
        }

        RaiseQueueStateChanged();

        return new QueueEnqueueResult(
            addedFiles,
            queuedEmptyDirectories,
            skippedEmptyDirectories,
            addedGroups);
    }

    private string GetUniqueGroupNameLocked(string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "项目";
        }

        if (!_groups.Any(group => string.Equals(
                group.DisplayName,
                baseName,
                StringComparison.OrdinalIgnoreCase)))
        {
            return baseName;
        }

        for (var index = 2; index < int.MaxValue; index++)
        {
            var candidate = $"{baseName} ({index})";

            if (!_groups.Any(group => string.Equals(
                    group.DisplayName,
                    candidate,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return baseName;
    }

    private static string GetRootRelativePath(TransferGroupPlan group)
    {
        var relative = group.Files.FirstOrDefault()?.RelativeDirectory;

        if (string.IsNullOrWhiteSpace(relative))
        {
            relative = group.EmptyDirectories.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(relative))
        {
            return string.Empty;
        }

        var normalized = relative.Replace('\\', '/');
        var separator = normalized.IndexOf('/');
        return separator < 0
            ? normalized
            : normalized[..separator];
    }

    private string GetAvailableDestinationRootLocked(string requestedRoot)
    {
        var usedRoots = new HashSet<string>(
            _groups
                .Where(group => group.IsFolder &&
                                !string.IsNullOrWhiteSpace(group.DestinationRoot))
                .Select(group => group.DestinationRoot!),
            StringComparer.OrdinalIgnoreCase);

        if (!usedRoots.Contains(requestedRoot))
        {
            return requestedRoot;
        }

        for (var index = 2; index < int.MaxValue; index++)
        {
            var candidate = $"{requestedRoot} ({index})";

            if (!usedRoots.Contains(candidate))
            {
                return candidate;
            }
        }

        return requestedRoot;
    }

    private static string ReplaceRoot(
        string relativePath,
        string oldRoot,
        string newRoot)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return relativePath;
        }

        var normalized = relativePath.Replace('\\', '/');

        if (normalized.Equals(oldRoot, StringComparison.OrdinalIgnoreCase))
        {
            return newRoot;
        }

        var prefix = oldRoot + "/";

        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? newRoot + normalized[oldRoot.Length..]
            : normalized;
    }

    private void RaiseQueueStateChanged()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(GroupCount));
        OnPropertyChanged(nameof(HasWaitingFiles));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CurrentIndex));
        OnPropertyChanged(nameof(CurrentPositionText));
        OnPropertyChanged(nameof(CurrentGroup));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(SkippedCount));
        OnPropertyChanged(nameof(FinishedCount));
        OnPropertyChanged(nameof(OverallProgressText));
        OnPropertyChanged(nameof(OverallProgressPercent));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(OverallSpeedBytesPerSecond));
        OnPropertyChanged(nameof(OverallSpeedText));
        OnPropertyChanged(nameof(OverallEtaText));
        OnPropertyChanged(nameof(OverallTransferText));
        OnPropertyChanged(nameof(EmptyDirectoryCount));
        OnPropertyChanged(nameof(SkippedEmptyDirectoryCount));
        OnPropertyChanged(nameof(HasEmptyDirectories));
        OnPropertyChanged(nameof(ShowEmptyDirectorySummary));
        OnPropertyChanged(nameof(EmptyDirectorySummary));
        OnPropertyChanged(nameof(SelectedFile));
        OnPropertyChanged(nameof(SelectedGroup));
        OnPropertyChanged(nameof(DetailsFile));
        OnPropertyChanged(nameof(DetailsPositionText));
        OnPropertyChanged(nameof(DetailsGroup));
        OnPropertyChanged(nameof(DetailsGroupPositionText));
    }

    private static TransferSource CreateSourceFromPath(string path)
    {
        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            fullPath = path;
        }

        string fileName;

        try
        {
            fileName = Path.GetFileName(fullPath);
        }
        catch (Exception)
        {
            fileName = fullPath;
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = fullPath;
        }

        return new TransferSource(fullPath, fileName, string.Empty);
    }

    /// <summary>
    /// 清空尚未开始的队列和历史统计。运行期间调用会抛出异常。
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("传输队列运行期间不能清空队列。");
            }

            _pending.Clear();
            _pendingManifests.Clear();
            _groups.Clear();
            _groupsById.Clear();
            _emptyDirectoryCount = 0;
            _skippedEmptyDirectoryCount = 0;
            _sentEmptyDirectoryCount = 0;
        }

        CurrentFile = null;
        _selectedFile = null;
        _selectedGroup = null;
        _files.Clear();
        _completedCount = 0;
        _failedCount = 0;
        _skippedCount = 0;
        _overallRate.Reset();
        _overallWatch.Reset();

        OnPropertyChanged(nameof(SelectedFile));
        OnPropertyChanged(nameof(SelectedGroup));
        OnPropertyChanged(nameof(DetailsFile));
        OnPropertyChanged(nameof(DetailsPositionText));
        OnPropertyChanged(nameof(DetailsGroup));
        OnPropertyChanged(nameof(DetailsGroupPositionText));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(GroupCount));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(HasWaitingFiles));
        OnPropertyChanged(nameof(HasEmptyDirectories));
        OnPropertyChanged(nameof(ShowEmptyDirectorySummary));
        OnPropertyChanged(nameof(EmptyDirectorySummary));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CurrentPositionText));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(SkippedCount));
        OnPropertyChanged(nameof(FinishedCount));
        OnPropertyChanged(nameof(OverallProgressText));
        OnPropertyChanged(nameof(OverallProgressPercent));
        OnPropertyChanged(nameof(OverallSpeedBytesPerSecond));
        OnPropertyChanged(nameof(OverallSpeedText));
        OnPropertyChanged(nameof(OverallEtaText));
        OnPropertyChanged(nameof(OverallTransferText));
        OnPropertyChanged(nameof(EmptyDirectoryCount));
        OnPropertyChanged(nameof(SkippedEmptyDirectoryCount));
    }

    /// <summary>
    /// 请求在当前文件完成后暂停。暂停期间不会发送下一个文件的 FileStart。
    /// </summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (!_isRunning || _pauseRequested)
            {
                return;
            }

            _pauseRequested = true;

            if (_pauseTcs is null || _pauseTcs.Task.IsCompleted)
            {
                _pauseTcs = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        _log?.Invoke("已请求暂停，当前块完成后停止发送。");
    }

    /// <summary>
    /// 继续已暂停的队列。
    /// </summary>
    public void Resume()
    {
        TaskCompletionSource? pauseTcs = null;

        lock (_gate)
        {
            if (!_pauseRequested)
            {
                return;
            }

            _pauseRequested = false;
            pauseTcs = _pauseTcs;
        }

        pauseTcs?.TrySetResult();
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        _log?.Invoke("队列已继续。");
    }

    /// <summary>
    /// 在等待队列中上移或下移一个文件。单文件分组等同于移动顶层项目；
    /// 多文件分组只调整组内文件顺序。
    /// </summary>
    public bool MoveWaiting(PendingFile file, int offset)
    {
        ArgumentNullException.ThrowIfNull(file);

        var changed = false;

        lock (_gate)
        {
            if (file.Status != PendingFileStatus.Waiting)
            {
                return false;
            }

            var group = FindGroup(file);

            if (group is null)
            {
                return false;
            }

            changed = group.Files.Count == 1
                ? MoveGroupLocked(group, offset)
                : group.MoveFile(file, offset);

            if (changed && group.Files.Count > 1)
            {
                RebuildFlatQueueLocked();
            }
        }

        if (changed)
        {
            RaiseQueueStateChanged();
        }

        return changed;
    }

    /// <summary>上移或下移一个顶层项目。仅全部等待中的项目可移动。</summary>
    public bool MoveGroup(QueueGroup group, int offset)
    {
        ArgumentNullException.ThrowIfNull(group);

        var changed = false;

        lock (_gate)
        {
            changed = MoveGroupLocked(group, offset);
        }

        if (changed)
        {
            RaiseQueueStateChanged();
        }

        return changed;
    }

    public bool CanMoveGroup(QueueGroup group, int offset)
    {
        ArgumentNullException.ThrowIfNull(group);

        lock (_gate)
        {
            if (!CanMoveGroupLocked(group))
            {
                return false;
            }

            var movable = _groups
                .Where(CanMoveGroupLocked)
                .ToList();
            var index = movable.IndexOf(group);
            var targetIndex = index + offset;

            return index >= 0 &&
                   targetIndex >= 0 &&
                   targetIndex < movable.Count;
        }
    }

    private bool MoveGroupLocked(QueueGroup group, int offset)
    {
        if (!CanMoveGroupLocked(group))
        {
            return false;
        }

        var movable = _groups
            .Where(CanMoveGroupLocked)
            .ToList();
        var index = movable.IndexOf(group);
        var targetIndex = index + offset;

        if (index < 0 ||
            targetIndex < 0 ||
            targetIndex >= movable.Count)
        {
            return false;
        }

        var target = movable[targetIndex];
        var sourceListIndex = _groups.IndexOf(group);
        var targetListIndex = _groups.IndexOf(target);

        if (sourceListIndex < 0 || targetListIndex < 0)
        {
            return false;
        }

        _groups.Move(sourceListIndex, targetListIndex);
        RebuildFlatQueueLocked();
        return true;
    }

    private bool CanMoveGroupLocked(QueueGroup group)
    {
        if (!_groups.Contains(group))
        {
            return false;
        }

        if (group.Files.Count == 0)
        {
            return group.EmptyDirectoryCount > 0 &&
                   !group.EmptyDirectoriesSent;
        }

        return group.Files.All(
            file => file.Status == PendingFileStatus.Waiting);
    }

    /// <summary>移除一个顶层项目。仅全部等待中的项目可移除。</summary>
    public bool RemoveGroup(QueueGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var removed = false;

        lock (_gate)
        {
            if (!CanRemoveGroupLocked(group))
            {
                return false;
            }

            RemoveGroupLocked(group);
            removed = true;
        }

        if (removed)
        {
            RaiseQueueStateChanged();
        }

        return removed;
    }

    public bool CanRemoveGroup(QueueGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        lock (_gate)
        {
            return CanRemoveGroupLocked(group);
        }
    }

    private bool CanRemoveGroupLocked(QueueGroup group)
    {
        if (!_groups.Contains(group))
        {
            return false;
        }

        if (group.Files.Count == 0)
        {
            return group.EmptyDirectoryCount > 0 &&
                   !group.EmptyDirectoriesSent;
        }

        return group.Files.All(
            file => file.Status == PendingFileStatus.Waiting);
    }

    private void RemoveGroupLocked(QueueGroup group)
    {
        var selectedFileInGroup = _selectedFile is not null &&
                                  group.Files.Contains(_selectedFile);

        foreach (var file in group.Files.ToArray())
        {
            _files.Remove(file);
            group.RemoveFile(file);
        }

        _groups.Remove(group);
        _groupsById.Remove(group.Id);
        _emptyDirectoryCount = Math.Max(
            0,
            _emptyDirectoryCount - group.EmptyDirectoryCount);

        if (group.EmptyDirectoriesSent)
        {
            _sentEmptyDirectoryCount = Math.Max(
                0,
                _sentEmptyDirectoryCount - group.EmptyDirectoryCount);
        }

        if (ReferenceEquals(_selectedGroup, group))
        {
            _selectedGroup = null;
        }

        if (selectedFileInGroup)
        {
            _selectedFile = null;
        }

        RebuildPendingQueueLocked();
        RebuildPendingManifestsLocked();
    }

    /// <summary>
    /// 从等待队列中移除一个尚未开始的文件。
    /// </summary>
    public bool RemoveWaiting(PendingFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        lock (_gate)
        {
            if (file.Status != PendingFileStatus.Waiting ||
                !_files.Remove(file))
            {
                return false;
            }

            var group = FindGroup(file);

            if (group is not null)
            {
                group.RemoveFile(file);

                if (group.Files.Count == 0 &&
                    group.EmptyDirectoryCount == 0)
                {
                    _groups.Remove(group);
                    _groupsById.Remove(group.Id);

                    if (ReferenceEquals(_selectedGroup, group))
                    {
                        _selectedGroup = null;
                    }
                }
            }

            if (ReferenceEquals(_selectedFile, file))
            {
                _selectedFile = null;
            }

            RebuildPendingQueueLocked();
            RebuildPendingManifestsLocked();
        }

        RaiseQueueStateChanged();
        return true;
    }

    /// <summary>
    /// 从队列中移除一个已完成、失败或跳过的文件，并同步修正统计。
    /// </summary>
    public bool RemoveFinished(PendingFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var currentChanged = false;

        lock (_gate)
        {
            if (file.Status is PendingFileStatus.Waiting or
                PendingFileStatus.Transferring)
            {
                return false;
            }

            if (!_files.Remove(file))
            {
                return false;
            }

            switch (file.Status)
            {
                case PendingFileStatus.Completed:
                    _completedCount = Math.Max(0, _completedCount - 1);
                    break;
                case PendingFileStatus.Failed:
                    _failedCount = Math.Max(0, _failedCount - 1);
                    break;
                case PendingFileStatus.Skipped:
                    _skippedCount = Math.Max(0, _skippedCount - 1);
                    break;
            }

            var group = FindGroup(file);

            if (group is not null)
            {
                group.RemoveFile(file);

                if (group.Files.Count == 0 &&
                    group.EmptyDirectoryCount == 0)
                {
                    _groups.Remove(group);
                    _groupsById.Remove(group.Id);

                    if (ReferenceEquals(_selectedGroup, group))
                    {
                        _selectedGroup = null;
                    }
                }
            }

            if (ReferenceEquals(_selectedFile, file))
            {
                _selectedFile = null;
            }

            if (ReferenceEquals(_currentFile, file))
            {
                _currentFile = null;
                currentChanged = true;
            }

            RebuildPendingQueueLocked();
            RebuildPendingManifestsLocked();
        }

        if (currentChanged)
        {
            OnPropertyChanged(nameof(CurrentFile));
            OnPropertyChanged(nameof(CurrentIndex));
            OnPropertyChanged(nameof(CurrentPositionText));
            OnPropertyChanged(nameof(CurrentGroup));
        }

        RaiseQueueStateChanged();
        return true;
    }

    private async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task pauseTask;

        lock (_gate)
        {
            if (!_pauseRequested)
            {
                return;
            }

            _pauseTcs ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pauseTask = _pauseTcs.Task;
        }

        await pauseTask.WaitAsync(cancellationToken).ConfigureAwait(true);
    }

    private void RebuildPendingQueue()
    {
        RebuildPendingQueueLocked();
    }

    private void RebuildPendingQueueLocked()
    {
        _pending.Clear();

        foreach (var group in _groups)
        {
            foreach (var file in group.Files)
            {
                if (file.Status == PendingFileStatus.Waiting)
                {
                    _pending.Enqueue(file);
                }
            }
        }
    }

    private void RebuildPendingManifestsLocked()
    {
        _pendingManifests.Clear();

        foreach (var group in _groups)
        {
            if (group.EmptyDirectoryCount > 0 &&
                !group.EmptyDirectoriesSent)
            {
                _pendingManifests.Enqueue(
                    new PendingManifestBatch(
                        group.Id,
                        group.EmptyDirectories));
            }
        }
    }

    private void RebuildFlatQueueLocked()
    {
        _files.Clear();

        foreach (var group in _groups)
        {
            foreach (var file in group.Files)
            {
                _files.Add(file);
            }
        }

        RebuildPendingQueueLocked();
        RebuildPendingManifestsLocked();
    }

    private void UpdateOverallRate()
    {
        long transferred = 0;

        lock (_gate)
        {
            foreach (var file in _files)
            {
                transferred += file.BytesTransferred;
            }
        }

        _overallRate.Update(transferred, _overallWatch.Elapsed);

        OnPropertyChanged(nameof(OverallSpeedBytesPerSecond));
        OnPropertyChanged(nameof(OverallSpeedText));
        OnPropertyChanged(nameof(OverallEtaText));
        OnPropertyChanged(nameof(OverallTransferText));
    }

    /// <summary>
    /// 串行发送队列中的文件。取消令牌触发时停止当前文件，剩余文件标记为 Skipped；
    /// 方法本身不会因用户取消而抛出 <see cref="OperationCanceledException"/>。
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("传输队列已在运行。");
            }

            _isRunning = true;
            _pauseRequested = false;
            _pauseTcs = null;
        }

        long initialTransferred = 0;

        lock (_gate)
        {
            foreach (var pendingFile in _files)
            {
                initialTransferred += pendingFile.BytesTransferred;
            }
        }

        _overallRate.Reset(initialTransferred);
        _overallWatch.Restart();

        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(HasWaitingFiles));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(OverallSpeedBytesPerSecond));
        OnPropertyChanged(nameof(OverallSpeedText));
        OnPropertyChanged(nameof(OverallEtaText));
        OnPropertyChanged(nameof(OverallTransferText));
        OnPropertyChanged(nameof(EmptyDirectoryCount));
        OnPropertyChanged(nameof(HasEmptyDirectories));
        OnPropertyChanged(nameof(ShowEmptyDirectorySummary));
        OnPropertyChanged(nameof(EmptyDirectorySummary));

        var canceled = false;
        string? stopReason = null;
        var queueInfoSent = false;
        var anyFileStartReported = 0;
        long queueBytes = 0;
        var hasPending = false;

        lock (_gate)
        {
            RebuildPendingManifestsLocked();
            hasPending = _pending.Count > 0;

            foreach (var pendingFile in _pending)
            {
                queueBytes += pendingFile.Size;
            }
        }

        try
        {
            if (hasPending)
            {
                await SendQueueInfoAsync(queueBytes, cancellationToken)
                    .ConfigureAwait(true);
                queueInfoSent = true;
            }

            while (true)
            {
                PendingFile? file;

                await WaitIfPausedAsync(cancellationToken).ConfigureAwait(true);
                await SendPendingManifestsAsync(cancellationToken)
                    .ConfigureAwait(true);

                lock (_gate)
                {
                    if (_pending.Count == 0)
                    {
                        // 与 EnqueueFiles 使用同一把锁收回“可追加”状态：
                        // 此后的追加会作为新的等待队列，由用户再次开始。
                        _isRunning = false;
                        break;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        canceled = true;
                        break;
                    }

                    file = _pending.Dequeue();
                }

                var fileStopReason = await TransferFileAsync(
                        file,
                        cancellationToken,
                        () => Interlocked.Exchange(ref anyFileStartReported, 1))
                    .ConfigureAwait(true);

                if (fileStopReason is not null)
                {
                    stopReason = fileStopReason;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            canceled = true;
        }
        catch (Exception exception)
        {
            stopReason = ResolveErrorMessage(exception);
        }
        finally
        {
            // 没有真正发出 FileStart 时，清除接收端的队列总量，避免影响下一次单文件发送。
            if (queueInfoSent &&
                Volatile.Read(ref anyFileStartReported) == 0)
            {
                try
                {
                    using var clearCts = new CancellationTokenSource(
                        TimeSpan.FromSeconds(1));
                    await SendQueueInfoAsync(0, clearCts.Token)
                        .ConfigureAwait(true);
                }
                catch (Exception)
                {
                    // 清理队列信息失败不应覆盖真实传输结果。
                }
            }

            lock (_gate)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                }

                // 正常排空时，完成瞬间追加的文件保留为等待状态，由用户再次开始；
                // 取消或错误停止时仍把剩余文件标记为跳过。
                if (canceled || stopReason is not null)
                {
                    _pendingManifests.Clear();

                    while (_pending.Count > 0)
                    {
                        var file = _pending.Dequeue();
                        file.Status = PendingFileStatus.Skipped;
                        file.ErrorMessage = canceled
                            ? "队列已取消，未传输。"
                            : $"传输已停止：{stopReason}";
                        _skippedCount++;
                    }
                }

                _isRunning = false;
                _pauseRequested = false;
                _pauseTcs = null;
            }

            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(HasWaitingFiles));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(CompletedCount));
            OnPropertyChanged(nameof(FailedCount));
            OnPropertyChanged(nameof(SkippedCount));
            OnPropertyChanged(nameof(FinishedCount));
            OnPropertyChanged(nameof(OverallProgressText));
            OnPropertyChanged(nameof(OverallProgressPercent));
            OnPropertyChanged(nameof(OverallSpeedBytesPerSecond));
            OnPropertyChanged(nameof(OverallSpeedText));
            OnPropertyChanged(nameof(OverallEtaText));
            OnPropertyChanged(nameof(OverallTransferText));
            OnPropertyChanged(nameof(EmptyDirectoryCount));
            OnPropertyChanged(nameof(HasEmptyDirectories));
            OnPropertyChanged(nameof(ShowEmptyDirectorySummary));
            OnPropertyChanged(nameof(EmptyDirectorySummary));

            RaiseQueueCompleted(
                new TransferQueueCompletedEventArgs(
                    TotalCount,
                    CompletedCount,
                    FailedCount,
                    SkippedCount,
                    canceled,
                    _sentEmptyDirectoryCount,
                    stopReason));
        }
    }

    private async Task SendPendingManifestsAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            PendingManifestBatch? batch;

            lock (_gate)
            {
                if (_pendingManifests.Count == 0)
                {
                    return;
                }

                batch = _pendingManifests.Peek();
            }

            await SendDirectoryManifestAsync(
                    batch.Directories,
                    cancellationToken)
                .ConfigureAwait(true);

            lock (_gate)
            {
                if (_groupsById.TryGetValue(batch.GroupId, out var group))
                {
                    group.MarkEmptyDirectoriesSent();
                }

                var remaining = _pendingManifests
                    .Where(item => item.GroupId != batch.GroupId)
                    .ToArray();
                _pendingManifests.Clear();

                foreach (var item in remaining)
                {
                    _pendingManifests.Enqueue(item);
                }

                _sentEmptyDirectoryCount += batch.Directories.Count;
            }

            OnPropertyChanged(nameof(OverallProgressText));
            OnPropertyChanged(nameof(OverallProgressPercent));
            OnPropertyChanged(nameof(EmptyDirectorySummary));
        }
    }

    private async Task SendDirectoryManifestAsync(
        IReadOnlyList<string> directories,
        CancellationToken cancellationToken)
    {
        if (directories.Count == 0)
        {
            return;
        }

        var maximumPayloadLength = Math.Min(
            _dispatcher.MaxPayloadLength,
            256 * 1024);
        var chunk = new List<string>();
        var payloadLength = DirectoryManifestCodec.HeaderLength;

        foreach (var directory in directories)
        {
            var entryLength = DirectoryManifestCodec.GetEncodedEntryLength(
                directory);

            if (chunk.Count > 0 &&
                payloadLength + entryLength > maximumPayloadLength)
            {
                await SendDirectoryManifestChunkAsync(
                        chunk,
                        cancellationToken)
                    .ConfigureAwait(true);
                chunk.Clear();
                payloadLength = DirectoryManifestCodec.HeaderLength;
            }

            chunk.Add(directory);
            payloadLength += entryLength;
        }

        if (chunk.Count > 0)
        {
            await SendDirectoryManifestChunkAsync(chunk, cancellationToken)
                .ConfigureAwait(true);
        }

        _log?.Invoke($"已发送目录清单：{directories.Count} 个空文件夹。");
    }

    private Task SendDirectoryManifestChunkAsync(
        IReadOnlyList<string> directories,
        CancellationToken cancellationToken) =>
        _dispatcher.SendAsync(
            MessageType.ControlMessage,
            ControlMessageCodec.SerializeDirectoryManifest(directories),
            cancellationToken);

    private async Task<string?> TransferFileAsync(
        PendingFile file,
        CancellationToken cancellationToken,
        Action onFileStartReported)
    {
        CurrentFile = file;
        file.StartOffset = 0;
        file.BytesTransferred = 0;
        file.ErrorMessage = null;
        file.Status = PendingFileStatus.Transferring;
        var transferWatch = Stopwatch.StartNew();

        OnPropertyChanged(nameof(HasWaitingFiles));
        OnPropertyChanged(nameof(CanStart));

        var startReported = 0;
        var startProgress = new InlineProgress<FileTransferStartInfo>(info =>
        {
            file.StartOffset = info.StartOffset;

            if (Interlocked.Exchange(ref startReported, 1) == 0)
            {
                RaiseFileStarted(new QueueFileStartedEventArgs(file, info));
                onFileStartReported();
            }
        });

        var progress = new ThrottledProgress<FileTransferProgress>(
            value =>
            {
                file.BytesTransferred = value.BytesTransferred;
                UpdateOverallRate();
            },
            TimeSpan.FromMilliseconds(150),
            value => value.TotalBytes > 0 &&
                     value.BytesTransferred >= value.TotalBytes);

        try
        {
            var result = await FileSender
                .SendFileAsync(
                    _dispatcher,
                    file.SourcePath,
                    _maxFileSize,
                    progress,
                    startProgress,
                    cancellationToken,
                    _log,
                    relativeDirectory: file.RelativeDirectory,
                    pauseAsync: WaitIfPausedAsync,
                    windowedTransfer: _supportsWindowedTransfer)
                .ConfigureAwait(true);

            file.BytesTransferred = result.FileSize;
            file.StartOffset = result.StartOffset;
            file.Status = PendingFileStatus.Completed;
            _completedCount++;
            UpdateOverallRate();
            transferWatch.Stop();

            RaiseFileCompleted(
                new QueueFileCompletedEventArgs(
                    file,
                    result,
                    transferWatch.Elapsed));
            return null;
        }
        catch (FileSkippedException exception)
        {
            file.Status = PendingFileStatus.Skipped;
            file.ErrorMessage = exception.Message;
            _skippedCount++;

            RaiseFileSkipped(
                new QueueFileSkippedEventArgs(file, exception.Message));
            return null;
        }
        catch (RemoteTransferException exception)
            when (exception.ErrorCode == WifiDirectErrorCode.StorageInsufficient)
        {
            file.Status = PendingFileStatus.Failed;
            file.ErrorMessage = exception.Message;
            _failedCount++;

            RaiseFileFailed(new QueueFileFailedEventArgs(file, exception));
            return exception.Message;
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            file.Status = PendingFileStatus.Failed;
            file.ErrorMessage = "操作已取消。";
            _failedCount++;

            RaiseFileFailed(new QueueFileFailedEventArgs(file, exception));
            return null;
        }
        catch (Exception exception)
        {
            file.Status = PendingFileStatus.Failed;
            file.ErrorMessage = ResolveErrorMessage(exception);
            _failedCount++;

            RaiseFileFailed(new QueueFileFailedEventArgs(file, exception));
            return null;
        }
        finally
        {
            OnPropertyChanged(nameof(CompletedCount));
            OnPropertyChanged(nameof(FailedCount));
            OnPropertyChanged(nameof(SkippedCount));
            OnPropertyChanged(nameof(FinishedCount));
            OnPropertyChanged(nameof(OverallProgressText));
            OnPropertyChanged(nameof(OverallProgressPercent));
            OnPropertyChanged(nameof(HasWaitingFiles));
            OnPropertyChanged(nameof(CanStart));
        }
    }

    private Task SendQueueInfoAsync(long totalBytes, CancellationToken cancellationToken) =>
        _dispatcher.SendAsync(
            MessageType.ControlMessage,
            ControlMessageCodec.SerializeQueueInfo(totalBytes),
            cancellationToken);

    private string ResolveErrorMessage(Exception exception)
    {
        if (_errorMessageFactory is not null)
        {
            try
            {
                var mapped = _errorMessageFactory(exception);

                if (!string.IsNullOrWhiteSpace(mapped))
                {
                    return mapped;
                }
            }
            catch (Exception)
            {
                // 错误映射失败时回退到默认消息。
            }
        }

        return exception switch
        {
            OperationCanceledException => "操作已取消。",
            FileNotFoundException => "找不到源文件。",
            DirectoryNotFoundException => "找不到源文件所在的文件夹。",
            UnauthorizedAccessException => "没有权限读取源文件。",
            FileTransferException => exception.Message,
            WifiDirectException => exception.Message,
            _ when !string.IsNullOrWhiteSpace(exception.Message) => exception.Message,
            _ => "传输失败。"
        };
    }

    private static PendingFile CreatePendingFile(string path)
    {
        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            fullPath = path;
        }

        string fileName;

        try
        {
            fileName = Path.GetFileName(fullPath);
        }
        catch (Exception)
        {
            fileName = fullPath;
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = fullPath;
        }

        long size = 0;

        try
        {
            var info = new FileInfo(fullPath);

            if (info.Exists)
            {
                size = info.Length;
            }
        }
        catch (Exception)
        {
            // 大小读取失败不影响入队，发送时会给出具体错误。
        }

        return new PendingFile(fullPath, fileName, size);
    }

    private static PendingFile CreatePendingFile(TransferSource source)
    {
        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(source.SourcePath);
        }
        catch (Exception)
        {
            fullPath = source.SourcePath;
        }

        var fileName = string.IsNullOrWhiteSpace(source.FileName)
            ? Path.GetFileName(fullPath)
            : source.FileName;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = fullPath;
        }

        long size = 0;

        try
        {
            var info = new FileInfo(fullPath);

            if (info.Exists)
            {
                size = info.Length;
            }
        }
        catch (Exception)
        {
            // 大小读取失败不影响入队，发送时会给出具体错误。
        }

        return new PendingFile(
            fullPath,
            fileName,
            size,
            source.RelativeDirectory);
    }

    private void RaiseFileStarted(QueueFileStartedEventArgs args)
    {
        try
        {
            FileStarted?.Invoke(this, args);
        }
        catch (Exception)
        {
            // 订阅方异常不应中断队列。
        }
    }

    private void RaiseFileCompleted(QueueFileCompletedEventArgs args)
    {
        try
        {
            FileCompleted?.Invoke(this, args);
        }
        catch (Exception)
        {
            // 订阅方异常不应中断队列。
        }
    }

    private void RaiseFileFailed(QueueFileFailedEventArgs args)
    {
        try
        {
            FileFailed?.Invoke(this, args);
        }
        catch (Exception)
        {
            // 订阅方异常不应中断队列。
        }
    }

    private void RaiseFileSkipped(QueueFileSkippedEventArgs args)
    {
        try
        {
            FileSkipped?.Invoke(this, args);
        }
        catch (Exception)
        {
            // 订阅方异常不应中断队列。
        }
    }

    private void RaiseQueueCompleted(TransferQueueCompletedEventArgs args)
    {
        try
        {
            QueueCompleted?.Invoke(this, args);
        }
        catch (Exception)
        {
            // 订阅方异常不应中断队列。
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed record PendingManifestBatch(
        Guid GroupId,
        IReadOnlyList<string> Directories);

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public InlineProgress(Action<T> report)
        {
            _report = report;
        }

        public void Report(T value) => _report(value);
    }
}

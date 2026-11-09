using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly Func<Exception, string>? _errorMessageFactory;
    private readonly Action<string>? _log;
    private readonly Queue<PendingFile> _pending = new();
    private readonly ObservableCollection<PendingFile> _files = new();
    private readonly ReadOnlyObservableCollection<PendingFile> _readOnlyFiles;
    private readonly object _gate = new();

    private PendingFile? _currentFile;
    private bool _isRunning;
    private bool _pauseRequested;
    private TaskCompletionSource? _pauseTcs;
    private int _completedCount;
    private int _failedCount;
    private int _skippedCount;

    public TransferQueueManager(
        MessageDispatcher dispatcher,
        long maxFileSize = FileTransferProtocol.DefaultMaxFileSize,
        Func<Exception, string>? errorMessageFactory = null,
        Action<string>? log = null)
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
        _errorMessageFactory = errorMessageFactory;
        _log = log;
        _readOnlyFiles = new ReadOnlyObservableCollection<PendingFile>(_files);
    }

    /// <summary>
    /// 供 UI 绑定。只能在与 UI 相同的线程上通过 <see cref="EnqueueFiles"/> /
    /// <see cref="Clear"/> 修改。
    /// </summary>
    public ReadOnlyObservableCollection<PendingFile> Files => _readOnlyFiles;

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

    public int FinishedCount => _completedCount + _failedCount + _skippedCount;

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
                return _isRunning || _pending.Count > 0;
            }
        }
    }

    public bool CanStart
    {
        get
        {
            lock (_gate)
            {
                return !_isRunning && _pending.Count > 0;
            }
        }
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
                return "尚未添加文件";
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

            return text;
        }
    }

    /// <summary>
    /// 总体进度按已处理（完成、失败、跳过）文件数计算，失败文件不会卡住进度条。
    /// </summary>
    public double OverallProgressPercent => TotalCount == 0
        ? 0d
        : Math.Clamp((double)FinishedCount / TotalCount * 100d, 0d, 100d);

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<QueueFileStartedEventArgs>? FileStarted;

    public event EventHandler<QueueFileCompletedEventArgs>? FileCompleted;

    public event EventHandler<QueueFileFailedEventArgs>? FileFailed;

    public event EventHandler<QueueFileSkippedEventArgs>? FileSkipped;

    public event EventHandler<TransferQueueCompletedEventArgs>? QueueCompleted;

    /// <summary>
    /// 将文件加入队尾。运行期间也可以追加，队列会在当前文件完成后继续。
    /// 路径不存在时仍会入队，并在发送时标记为失败，
    /// 这样单个无效路径不会阻止其他文件。
    /// </summary>
    public IReadOnlyList<PendingFile> EnqueueFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var added = new List<PendingFile>();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            added.Add(CreatePendingFile(path));
        }

        return AddPendingFiles(added);
    }

    /// <summary>
    /// 将带相对目录的源文件加入队尾。相对目录为空表示发送到接收目录根。
    /// </summary>
    public IReadOnlyList<PendingFile> EnqueueItems(
        IEnumerable<TransferSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var added = new List<PendingFile>();

        foreach (var source in sources)
        {
            if (source is null ||
                string.IsNullOrWhiteSpace(source.SourcePath))
            {
                continue;
            }

            added.Add(CreatePendingFile(source));
        }

        return AddPendingFiles(added);
    }

    private IReadOnlyList<PendingFile> AddPendingFiles(
        List<PendingFile> added)
    {
        if (added.Count == 0)
        {
            return added;
        }

        lock (_gate)
        {
            foreach (var file in added)
            {
                _files.Add(file);
                _pending.Enqueue(file);
            }
        }

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasWaitingFiles));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CurrentPositionText));
        OnPropertyChanged(nameof(OverallProgressText));
        OnPropertyChanged(nameof(OverallProgressPercent));

        return added;
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
        }

        CurrentFile = null;
        _files.Clear();
        _completedCount = 0;
        _failedCount = 0;
        _skippedCount = 0;

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasWaitingFiles));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CurrentPositionText));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(SkippedCount));
        OnPropertyChanged(nameof(FinishedCount));
        OnPropertyChanged(nameof(OverallProgressText));
        OnPropertyChanged(nameof(OverallProgressPercent));
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
        _log?.Invoke("已请求暂停，当前文件完成后暂停。");
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
    /// 在等待队列中上移或下移一个文件。offset 为 -1 或 +1。
    /// </summary>
    public bool MoveWaiting(PendingFile file, int offset)
    {
        ArgumentNullException.ThrowIfNull(file);

        lock (_gate)
        {
            if (file.Status != PendingFileStatus.Waiting)
            {
                return false;
            }

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
            var sourceListIndex = _files.IndexOf(file);
            var targetListIndex = _files.IndexOf(target);

            if (sourceListIndex < 0 || targetListIndex < 0)
            {
                return false;
            }

            _files.Move(sourceListIndex, targetListIndex);
            RebuildPendingQueue();
        }

        return true;
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

            RebuildPendingQueue();
        }

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasWaitingFiles));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CurrentPositionText));
        OnPropertyChanged(nameof(OverallProgressText));
        OnPropertyChanged(nameof(OverallProgressPercent));

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
        _pending.Clear();

        foreach (var file in _files)
        {
            if (file.Status == PendingFileStatus.Waiting)
            {
                _pending.Enqueue(file);
            }
        }
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

        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(HasWaitingFiles));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));

        var canceled = false;
        string? stopReason = null;
        var queueInfoSent = false;
        var anyFileStartReported = 0;
        long queueBytes = 0;
        var hasPending = false;

        lock (_gate)
        {
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

            RaiseQueueCompleted(
                new TransferQueueCompletedEventArgs(
                    TotalCount,
                    CompletedCount,
                    FailedCount,
                    SkippedCount,
                    canceled));
        }
    }

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
            value => file.BytesTransferred = value.BytesTransferred,
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
                    relativeDirectory: file.RelativeDirectory)
                .ConfigureAwait(true);

            file.BytesTransferred = result.FileSize;
            file.StartOffset = result.StartOffset;
            file.Status = PendingFileStatus.Completed;
            _completedCount++;

            RaiseFileCompleted(new QueueFileCompletedEventArgs(file, result));
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

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WifiDirectTransfer.Core;

/// <summary>一个已接收文件及其落盘时间。</summary>
public sealed class ReceivedFileEntry : INotifyPropertyChanged
{
    private bool _isMissing;

    public ReceivedFileEntry(
        FileReceiveResult result,
        DateTimeOffset receivedAt)
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

/// <summary>
/// 接收端顶层项目：一个直接接收的文件，或一个接收文件夹。
/// 文件夹不展开内部文件，只显示汇总。
/// </summary>
public sealed class ReceivedProject : INotifyPropertyChanged
{
    private readonly List<ReceivedFileEntry> _files = new();
    private readonly HashSet<string> _emptyDirectories =
        new(StringComparer.OrdinalIgnoreCase);
    private string _displayName;
    private string? _folderPath;
    private string? _pendingDisplayPath;
    private long _pendingBytes;
    private bool _isReceiving;
    private bool _isMissing;
    private DateTimeOffset? _lastActivityAt;

    public ReceivedProject(
        string displayName,
        bool isFolder,
        string rootRelativePath,
        string? folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        _displayName = displayName;
        IsFolder = isFolder;
        RootRelativePath = rootRelativePath;
        _folderPath = folderPath;
    }

    public string DisplayName
    {
        get => _displayName;
        private set
        {
            if (_displayName == value)
            {
                return;
            }

            _displayName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayPath));
        }
    }

    public bool IsFolder { get; }

    public string RootRelativePath { get; }

    public IReadOnlyList<ReceivedFileEntry> Files => _files;

    public int FileCount => _files.Count;

    public int EmptyDirectoryCount => _emptyDirectories.Count;

    public string? FolderPath
    {
        get => _folderPath;
        private set
        {
            if (_folderPath == value)
            {
                return;
            }

            _folderPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SourcePath));
        }
    }

    public string? LatestFilePath => _files.Count > 0
        ? _files[^1].FilePath
        : null;

    public string? SourcePath => IsFolder ? FolderPath : LatestFilePath;

    public string DisplayPath => IsFolder
        ? DisplayName
        : _files.Count > 0
            ? _files[^1].DisplayPath
            : _pendingDisplayPath ?? DisplayName;

    public long Size => _files.Sum(file => file.FileSize) +
                        (_isReceiving ? _pendingBytes : 0);

    public bool IsReceiving
    {
        get => _isReceiving;
        private set
        {
            if (_isReceiving == value)
            {
                return;
            }

            _isReceiving = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public DateTimeOffset? LastActivityAt
    {
        get => _lastActivityAt;
        private set
        {
            if (_lastActivityAt == value)
            {
                return;
            }

            _lastActivityAt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LastActivityText));
        }
    }

    public string LastActivityText => _lastActivityAt?.ToString("HH:mm:ss") ?? "—";

    public string Subtitle
    {
        get
        {
            if (IsFolder)
            {
                if (FileCount == 0)
                {
                    var emptyText = $"{EmptyDirectoryCount} 个空文件夹";
                    return IsMissing ? emptyText + " · 文件夹已不存在" : emptyText;
                }

                var text = $"{FileCount} 个文件";

                if (EmptyDirectoryCount > 0)
                {
                    text += $" · {EmptyDirectoryCount} 个空文件夹";
                }

                if (IsMissing)
                {
                    text += " · 文件夹已不存在";
                }

                return text;
            }

            return _files.Count > 0
                ? _files[^1].DetailText
                : "正在接收";
        }
    }

    public string StatusText
    {
        get
        {
            if (IsReceiving)
            {
                return "接收中";
            }

            if (FileCount > 0)
            {
                return "已接收";
            }

            return EmptyDirectoryCount > 0
                ? "空文件夹已创建"
                : "等待中";
        }
    }

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
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(Subtitle));
        }
    }

    public void BeginReceiving(
        string displayPath,
        long totalBytes,
        DateTimeOffset at)
    {
        _pendingDisplayPath = displayPath;
        _pendingBytes = Math.Max(0, totalBytes);
        IsReceiving = true;
        LastActivityAt = at;
        OnPropertyChanged(nameof(DisplayPath));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Size));
    }

    public void AddFile(ReceivedFileEntry entry, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _files.Add(entry);
        _pendingDisplayPath = null;
        _pendingBytes = 0;
        IsReceiving = false;
        IsMissing = false;
        LastActivityAt = at;

        if (!IsFolder)
        {
            DisplayName = entry.DisplayPath;
            FolderPath = Path.GetDirectoryName(entry.FilePath);
        }

        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(DisplayPath));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LatestFilePath));
        OnPropertyChanged(nameof(SourcePath));
    }

    public bool AddEmptyDirectory(
        string relativePath,
        string? folderPath,
        DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            !_emptyDirectories.Add(relativePath))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(FolderPath))
        {
            FolderPath = folderPath;
        }

        LastActivityAt = at;
        OnPropertyChanged(nameof(EmptyDirectoryCount));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(StatusText));
        return true;
    }

    public void EndReceiving(DateTimeOffset at)
    {
        if (!IsReceiving)
        {
            return;
        }

        _pendingDisplayPath = null;
        _pendingBytes = 0;
        IsReceiving = false;
        LastActivityAt = at;
        OnPropertyChanged(nameof(DisplayPath));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Size));
    }

    internal void Touch(DateTimeOffset at)
    {
        LastActivityAt = at;
    }

    public bool RefreshAvailability()
    {
        if (IsFolder)
        {
            var exists = !string.IsNullOrWhiteSpace(FolderPath) &&
                         Directory.Exists(FolderPath);
            IsMissing = !exists;
            return exists;
        }

        if (_files.Count == 0)
        {
            return true;
        }

        var allExist = _files.All(file => file.RefreshAvailability());
        IsMissing = !allExist;
        return allExist;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
}

/// <summary>接收端顶层项目集合。按实际落盘路径的顶层目录分组。</summary>
public sealed class ReceivedProjectCollection
{
    private readonly Func<string> _receiveDirectoryProvider;
    private readonly Dictionary<string, ReceivedProject> _folderProjects =
        new(StringComparer.OrdinalIgnoreCase);
    private ReceivedProject? _activeProject;

    public ReceivedProjectCollection(Func<string> receiveDirectoryProvider)
    {
        _receiveDirectoryProvider = receiveDirectoryProvider ??
                                    throw new ArgumentNullException(
                                        nameof(receiveDirectoryProvider));
    }

    public ObservableCollection<ReceivedProject> Projects { get; } = new();

    public ReceivedProject BeginFile(
        FileTransferStartInfo info,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (_activeProject is { } previous)
        {
            previous.EndReceiving(at);

            if (!previous.IsFolder && previous.FileCount == 0)
            {
                RemoveProject(previous);
            }
        }

        ReceivedProject project;

        if (string.IsNullOrWhiteSpace(info.RelativeDirectory))
        {
            project = new ReceivedProject(
                info.DisplayPath,
                isFolder: false,
                rootRelativePath: string.Empty,
                folderPath: null);
            Projects.Insert(0, project);
        }
        else
        {
            var root = GetTopLevelRoot(info.RelativeDirectory);
            project = GetOrCreateFolderProject(root, at);
        }

        project.BeginReceiving(info.DisplayPath, info.TotalBytes, at);
        _activeProject = project;
        return project;
    }

    public ReceivedProject? CompleteFile(
        FileReceiveResult result,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(result);

        var project = _activeProject;

        if (project is null)
        {
            if (string.IsNullOrWhiteSpace(result.RelativeDirectory))
            {
                project = new ReceivedProject(
                    result.DisplayPath,
                    isFolder: false,
                    rootRelativePath: string.Empty,
                    folderPath: null);
                Projects.Insert(0, project);
            }
            else
            {
                var root = GetTopLevelRoot(result.RelativeDirectory);
                project = GetOrCreateFolderProject(root, at);
            }
        }

        project.AddFile(new ReceivedFileEntry(result, at), at);
        _activeProject = null;
        return project;
    }

    public void FailActive(DateTimeOffset at)
    {
        var project = _activeProject;
        _activeProject = null;

        if (project is null)
        {
            return;
        }

        project.EndReceiving(at);

        if (project.FileCount == 0 && project.EmptyDirectoryCount == 0)
        {
            RemoveProject(project);
        }
    }

    public void SkipActive(DateTimeOffset at) => FailActive(at);

    public void AddEmptyDirectories(
        IReadOnlyList<string> relativeDirectories,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(relativeDirectories);

        foreach (var directory in relativeDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var root = GetTopLevelRoot(directory);

            if (root.Length == 0)
            {
                continue;
            }

            var project = GetOrCreateFolderProject(root, at);
            var folderPath = GetFolderPath(root);
            project.AddEmptyDirectory(directory, folderPath, at);
        }
    }

    public void ClearReceiving(DateTimeOffset at)
    {
        if (_activeProject is { } project)
        {
            project.EndReceiving(at);

            if (!project.IsFolder && project.FileCount == 0)
            {
                RemoveProject(project);
            }
        }

        _activeProject = null;
    }

    public void Clear()
    {
        _activeProject = null;
        _folderProjects.Clear();
        Projects.Clear();
    }

    public static string GetTopLevelRoot(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var normalized = relativePath.Replace('\\', '/');
        var separator = normalized.IndexOf('/');
        return separator < 0
            ? normalized
            : normalized[..separator];
    }

    private ReceivedProject GetOrCreateFolderProject(
        string root,
        DateTimeOffset at)
    {
        if (_folderProjects.TryGetValue(root, out var project))
        {
            return project;
        }

        project = new ReceivedProject(
            root,
            isFolder: true,
            rootRelativePath: root,
            folderPath: GetFolderPath(root));
        _folderProjects.Add(root, project);
        Projects.Insert(0, project);
        project.Touch(at);
        return project;
    }

    private string GetFolderPath(string root)
    {
        var receiveDirectory = _receiveDirectoryProvider();

        if (string.IsNullOrWhiteSpace(receiveDirectory))
        {
            return root;
        }

        return Path.Combine(receiveDirectory, root);
    }

    private void RemoveProject(ReceivedProject project)
    {
        Projects.Remove(project);

        if (project.IsFolder)
        {
            _folderProjects.Remove(project.RootRelativePath);
        }
    }
}

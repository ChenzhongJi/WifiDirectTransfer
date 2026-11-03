using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WifiDirectTransfer.Core;

public enum TransferDirection
{
    Send,
    Receive
}

public enum TransferOutcome
{
    Completed,
    Failed,
    Skipped
}

public sealed record TransferHistoryEntry(
    DateTimeOffset OccurredAt,
    TransferDirection Direction,
    string FileName,
    long FileSize,
    TransferOutcome Outcome,
    string? PeerName = null,
    string? FilePath = null,
    string? ErrorMessage = null);

/// <summary>
/// 持久化传输历史。写入失败只记录日志，不影响传输主流程。
/// </summary>
public sealed class TransferHistoryStore
{
    public const int MaxEntries = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly Action<string>? _log;

    private List<TransferHistoryEntry>? _entries;

    public TransferHistoryStore(
        string? filePath = null,
        Action<string>? log = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? GetDefaultPath()
            : filePath;
        _log = log;
    }

    public string FilePath => _filePath;

    public static string GetDefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WifiDirectTransfer",
        "history.json");

    public IReadOnlyList<TransferHistoryEntry> Load()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _entries!.ToArray();
        }
    }

    public void Add(TransferHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            EnsureLoaded();
            _entries!.Insert(0, entry);
            Trim();
            Save();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries = new List<TransferHistoryEntry>();
            Save();
        }
    }

    private void EnsureLoaded()
    {
        if (_entries is not null)
        {
            return;
        }

        var loaded = new List<TransferHistoryEntry>();

        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath, Encoding.UTF8);
                var parsed = JsonSerializer.Deserialize<List<TransferHistoryEntry>>(
                    json,
                    JsonOptions);

                if (parsed is not null)
                {
                    loaded.AddRange(parsed.OfType<TransferHistoryEntry>());
                }
            }
        }
        catch (Exception exception)
        {
            _log?.Invoke(
                "读取传输历史失败：" + exception.GetType().Name);
            loaded.Clear();
        }

        loaded.Sort(
            (left, right) => right.OccurredAt.CompareTo(left.OccurredAt));
        _entries = loaded;
        Trim();
    }

    private void Trim()
    {
        if (_entries!.Count > MaxEntries)
        {
            _entries.RemoveRange(
                MaxEntries,
                _entries.Count - MaxEntries);
        }
    }

    private void Save()
    {
        var tempPath = _filePath + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(_filePath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_entries, JsonOptions);
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception exception)
        {
            _log?.Invoke(
                "保存传输历史失败：" + exception.GetType().Name);
            TryDelete(tempPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 忽略清理失败。
        }
        catch (UnauthorizedAccessException)
        {
            // 忽略清理失败。
        }
    }
}

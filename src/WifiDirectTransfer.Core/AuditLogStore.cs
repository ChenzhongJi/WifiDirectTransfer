using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WifiDirectTransfer.Core;

public enum AuditEventType
{
    ConnectionAccepted,
    ConnectionRejected,
    ConnectionTimedOut,
    IdentityVerified,
    IdentityVerificationFailed,
    PairingAccepted,
    PairingRejected,
    PairingTimedOut,
    TrustAdded,
    TrustRemoved,
    TrustedAutoAccept,
    SensitiveFeatureEnabled,
    SensitiveFeatureDisabled
}

public sealed record AuditLogEntry(
    DateTimeOffset Timestamp,
    AuditEventType Event,
    string? PeerName,
    string? Fingerprint,
    string? Detail);

/// <summary>
/// 安全审计日志（JSON Lines）。只记录连接、身份、信任和敏感开关事件，
/// 不记录文件内容、剪贴板内容或文本正文。
/// </summary>
public sealed class AuditLogStore
{
    public const long DefaultMaxFileBytes = 1024 * 1024;
    public const int DefaultRetainedFiles = 3;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false
    };

    private readonly object _gate = new();
    private readonly long _maxFileBytes;
    private readonly int _retainedFiles;

    public AuditLogStore(
        string? filePath = null,
        long maxFileBytes = DefaultMaxFileBytes,
        int retainedFiles = DefaultRetainedFiles)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? GetDefaultFilePath()
            : Path.GetFullPath(filePath);
        _maxFileBytes = maxFileBytes > 0
            ? maxFileBytes
            : DefaultMaxFileBytes;
        _retainedFiles = Math.Max(1, retainedFiles);
    }

    public string FilePath { get; }

    /// <summary>
    /// 当前日志及其轮转文件，供诊断包收集。
    /// </summary>
    public IReadOnlyList<string> GetFilePaths()
    {
        var paths = new List<string>();

        lock (_gate)
        {
            for (var index = 0; index < _retainedFiles; index++)
            {
                paths.Add(index == 0
                    ? FilePath
                    : GetRotatedPath(index));
            }
        }

        return paths;
    }

    public bool Append(
        AuditEventType eventType,
        string? peerName = null,
        string? fingerprint = null,
        string? detail = null)
    {
        var entry = new AuditLogEntry(
            DateTimeOffset.Now,
            eventType,
            TrimTo(peerName, 255),
            TrustedDeviceStore.NormalizeFingerprint(fingerprint) is { Length: > 0 } normalized
                ? normalized
                : null,
            TrimTo(detail, 512));

        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(FilePath);

                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RotateIfNeeded();

                var json = JsonSerializer.Serialize(entry, SerializerOptions);
                File.AppendAllText(
                    FilePath,
                    json + Environment.NewLine,
                    new UTF8Encoding(false));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public IReadOnlyList<AuditLogEntry> ReadAll()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return Array.Empty<AuditLogEntry>();
                }

                var entries = new List<AuditLogEntry>();

                foreach (var line in File.ReadLines(FilePath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var entry = JsonSerializer.Deserialize<AuditLogEntry>(
                            line,
                            SerializerOptions);

                        if (entry is not null)
                        {
                            entries.Add(entry);
                        }
                    }
                    catch (JsonException)
                    {
                        // 单行损坏不影响其余记录。
                    }
                }

                return entries;
            }
            catch (Exception)
            {
                return Array.Empty<AuditLogEntry>();
            }
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(FilePath) ||
            new FileInfo(FilePath).Length < _maxFileBytes)
        {
            return;
        }

        for (var index = _retainedFiles - 1; index >= 1; index--)
        {
            var source = index == 1 ? FilePath : GetRotatedPath(index - 1);
            var target = GetRotatedPath(index);

            if (!File.Exists(source))
            {
                continue;
            }

            if (File.Exists(target))
            {
                File.Delete(target);
            }

            File.Move(source, target);
        }
    }

    private string GetRotatedPath(int index) => $"{FilePath}.{index}";

    private static string? TrimTo(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength];
    }

    private static string GetDefaultFilePath()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.GetTempPath();
        }

        return Path.Combine(
            localAppData,
            "WifiDirectTransfer",
            "audit.jsonl");
    }
}

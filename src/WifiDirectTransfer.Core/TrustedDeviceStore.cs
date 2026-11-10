using System.Text.Json;

namespace WifiDirectTransfer.Core;

/// <summary>
/// 已信任设备。指纹是公钥 SHA-256 的十六进制文本，也是信任关系的唯一键。
/// </summary>
public sealed class TrustedDevice
{
    public string Fingerprint { get; set; } = string.Empty;

    public string PublicKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string LastPeerId { get; set; } = string.Empty;

    public string LastPeerName { get; set; } = string.Empty;

    public DateTimeOffset TrustedAt { get; set; }

    public DateTimeOffset LastConnectedAt { get; set; }
}

/// <summary>
/// 可信设备本地 JSON 存储。指纹比较不区分大小写，写入时统一为大写。
/// </summary>
public sealed class TrustedDeviceStore
{
    private const int MaxDevices = 50;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;

    public TrustedDeviceStore(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? GetDefaultFilePath()
            : Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public IReadOnlyList<TrustedDevice> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return Array.Empty<TrustedDevice>();
            }

            var json = File.ReadAllText(_filePath);
            var devices = JsonSerializer.Deserialize<List<TrustedDevice>>(
                json,
                SerializerOptions);

            return Normalize(devices);
        }
        catch (Exception)
        {
            return Array.Empty<TrustedDevice>();
        }
    }

    public bool Save(IEnumerable<TrustedDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var normalized = Normalize(devices);
        var temporaryPath = _filePath + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(_filePath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(normalized, SerializerOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception)
            {
                // 保存失败时清理临时文件失败不应影响应用。
            }

            return false;
        }
    }

    /// <summary>
    /// 按指纹查找。指纹为空或格式无效时返回 null。
    /// </summary>
    public TrustedDevice? FindByFingerprint(
        string? fingerprint,
        IReadOnlyList<TrustedDevice>? devices = null)
    {
        var normalized = NormalizeFingerprint(fingerprint);

        if (normalized.Length == 0)
        {
            return null;
        }

        return (devices ?? Load()).FirstOrDefault(
            device => string.Equals(
                device.Fingerprint,
                normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 连接建立前只能依据 Peer Id 或显示名称预判可信设备。
    /// 显示名称只在唯一匹配时生效，避免同名设备误跳过接收确认。
    /// </summary>
    public TrustedDevice? FindByPeerCandidate(
        string? peerId,
        string? displayName,
        IReadOnlyList<TrustedDevice>? devices = null)
    {
        var all = devices ?? Load();

        if (!string.IsNullOrWhiteSpace(peerId))
        {
            var byId = all.FirstOrDefault(
                device => string.Equals(
                    device.LastPeerId,
                    peerId,
                    StringComparison.Ordinal));

            if (byId is not null)
            {
                return byId;
            }
        }

        if (string.IsNullOrWhiteSpace(displayName) ||
            string.Equals(
                displayName,
                PeerIdentityMatcher.UnnamedPeerDisplayName,
                StringComparison.Ordinal))
        {
            return null;
        }

        var name = displayName.Trim();
        var matches = all.Where(
                device =>
                    string.Equals(
                        device.DisplayName,
                        name,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        device.LastPeerName,
                        name,
                        StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }

    public bool AddOrUpdate(TrustedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var normalized = Normalize(new[] { device });
        var candidate = normalized.FirstOrDefault();

        if (candidate is null)
        {
            return false;
        }

        var devices = Load().ToList();
        var existingIndex = devices.FindIndex(
            item => string.Equals(
                item.Fingerprint,
                candidate.Fingerprint,
                StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
        {
            var existing = devices[existingIndex];
            candidate.TrustedAt = existing.TrustedAt == default
                ? candidate.TrustedAt
                : existing.TrustedAt;
            devices[existingIndex] = candidate;
        }
        else
        {
            devices.Add(candidate);
        }

        return Save(devices);
    }

    public bool Remove(string? fingerprint)
    {
        var normalized = NormalizeFingerprint(fingerprint);

        if (normalized.Length == 0)
        {
            return false;
        }

        var devices = Load()
            .Where(
                device => !string.Equals(
                    device.Fingerprint,
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return Save(devices);
    }

    public static string NormalizeFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(fingerprint.Length);

        foreach (var character in fingerprint)
        {
            if (Uri.IsHexDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<TrustedDevice> Normalize(
        IEnumerable<TrustedDevice>? devices)
    {
        if (devices is null)
        {
            return Array.Empty<TrustedDevice>();
        }

        var byFingerprint = new Dictionary<string, TrustedDevice>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices)
        {
            if (device is null)
            {
                continue;
            }

            var fingerprint = NormalizeFingerprint(device.Fingerprint);

            if (fingerprint.Length is < 16 or > 128)
            {
                continue;
            }

            var displayName = string.IsNullOrWhiteSpace(device.DisplayName)
                ? "未命名设备"
                : device.DisplayName.Trim();
            var lastPeerName = string.IsNullOrWhiteSpace(device.LastPeerName)
                ? displayName
                : device.LastPeerName.Trim();
            var trustedAt = device.TrustedAt == default
                ? DateTimeOffset.Now
                : device.TrustedAt;
            var candidate = new TrustedDevice
            {
                Fingerprint = fingerprint,
                PublicKey = device.PublicKey?.Trim() ?? string.Empty,
                DisplayName = displayName,
                LastPeerId = device.LastPeerId?.Trim() ?? string.Empty,
                LastPeerName = lastPeerName,
                TrustedAt = trustedAt,
                LastConnectedAt = device.LastConnectedAt
            };

            if (byFingerprint.TryGetValue(fingerprint, out var existing))
            {
                if (existing.LastConnectedAt > candidate.LastConnectedAt)
                {
                    candidate.LastConnectedAt = existing.LastConnectedAt;
                }

                if (existing.TrustedAt != default &&
                    existing.TrustedAt < candidate.TrustedAt)
                {
                    candidate.TrustedAt = existing.TrustedAt;
                }
            }

            byFingerprint[fingerprint] = candidate;
        }

        return byFingerprint.Values
            .OrderByDescending(device => device.LastConnectedAt)
            .ThenBy(device => device.DisplayName, StringComparer.CurrentCulture)
            .Take(MaxDevices)
            .ToArray();
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
            "trusted-devices.json");
    }
}

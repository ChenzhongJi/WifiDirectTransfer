using System.Text.Json;

namespace WifiDirectTransfer.Core;

public sealed class KnownDevice
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset LastConnectedAt { get; set; }
}

/// <summary>
/// 最近成功连接设备的本地 JSON 存储。
/// </summary>
public sealed class KnownDeviceStore
{
    private const int MaxDevices = 50;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;

    public KnownDeviceStore(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? GetDefaultFilePath()
            : Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public IReadOnlyList<KnownDevice> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return Array.Empty<KnownDevice>();
            }

            var json = File.ReadAllText(_filePath);
            var devices = JsonSerializer.Deserialize<List<KnownDevice>>(
                json,
                SerializerOptions);

            return Normalize(devices);
        }
        catch (Exception)
        {
            return Array.Empty<KnownDevice>();
        }
    }

    public bool Save(IEnumerable<KnownDevice> devices)
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

    private static IReadOnlyList<KnownDevice> Normalize(
        IEnumerable<KnownDevice>? devices)
    {
        if (devices is null)
        {
            return Array.Empty<KnownDevice>();
        }

        var byId = new Dictionary<string, KnownDevice>(StringComparer.Ordinal);

        foreach (var device in devices)
        {
            if (device is null ||
                string.IsNullOrWhiteSpace(device.Id))
            {
                continue;
            }

            var displayName = string.IsNullOrWhiteSpace(device.DisplayName)
                ? "未命名设备"
                : device.DisplayName.Trim();

            if (byId.TryGetValue(device.Id, out var existing) &&
                existing.LastConnectedAt >= device.LastConnectedAt)
            {
                continue;
            }

            byId[device.Id] = new KnownDevice
            {
                Id = device.Id,
                DisplayName = displayName,
                LastConnectedAt = device.LastConnectedAt
            };
        }

        return byId.Values
            .OrderByDescending(device => device.LastConnectedAt)
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
            "known-devices.json");
    }
}

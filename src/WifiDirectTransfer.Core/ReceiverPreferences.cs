using System.Text.Json;
using System.Text.Json.Serialization;

namespace WifiDirectTransfer.Core;

public enum AppThemeMode
{
    System,
    Light,
    Dark
}

public sealed class ReceiverPreferences
{
    public SameNamePolicy SameNamePolicy { get; set; } = SameNamePolicy.AutoRename;

    public bool ClipboardSyncEnabled { get; set; }

    public bool ClipboardConsentAcknowledged { get; set; }

    public bool TextReceiveEnabled { get; set; }

    public bool TextConsentAcknowledged { get; set; }

    public bool NotificationsEnabled { get; set; } = true;

    public bool TrustedAutoAcceptEnabled { get; set; } = true;

    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;

    public string? ReceiveDirectory { get; set; }
}

/// <summary>
/// 接收端本地设置的简单 JSON 存储。读取失败时回退到默认值，写入失败返回 false。
/// </summary>
public sealed class ReceiverPreferencesStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    private readonly string _filePath;

    public ReceiverPreferencesStore(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? GetDefaultFilePath()
            : Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public ReceiverPreferences Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new ReceiverPreferences();
            }

            var json = File.ReadAllText(_filePath);
            var preferences = JsonSerializer.Deserialize<ReceiverPreferences>(
                json,
                SerializerOptions);

            if (preferences is null ||
                !Enum.IsDefined(preferences.SameNamePolicy) ||
                !Enum.IsDefined(preferences.ThemeMode))
            {
                return new ReceiverPreferences();
            }

            return preferences;
        }
        catch (Exception)
        {
            return new ReceiverPreferences();
        }
    }

    public bool Save(ReceiverPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        if (!Enum.IsDefined(preferences.SameNamePolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(preferences),
                "同名文件策略无效。");
        }

        if (!Enum.IsDefined(preferences.ThemeMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(preferences),
                "主题设置无效。");
        }

        var temporaryPath = _filePath + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(_filePath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(preferences, SerializerOptions);
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
            "settings.json");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

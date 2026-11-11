using System.Text.Json;
using System.Text.Json.Serialization;

namespace WifiDirectTransfer.Core;

/// <summary>
/// 自动更新描述文件 <c>latest.json</c>。解析失败一律返回 <c>null</c>，
/// 不做任何网络或文件操作，便于单元测试。
/// </summary>
public sealed class UpdateManifest
{
    /// <summary>更新包大小上限，防止异常清单导致磁盘被写满。</summary>
    public const long MaxSetupBytes = 4L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private UpdateManifest(
        ReleaseVersion version,
        Uri setupUri,
        string sha256,
        long size,
        Uri? notesUri)
    {
        Version = version;
        SetupUri = setupUri;
        Sha256 = sha256;
        Size = size;
        NotesUri = notesUri;
    }

    public ReleaseVersion Version { get; }

    public Uri SetupUri { get; }

    /// <summary>小写十六进制 SHA-256。</summary>
    public string Sha256 { get; }

    public long Size { get; }

    public Uri? NotesUri { get; }

    public static UpdateManifest? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        ManifestDto? dto;

        try
        {
            dto = JsonSerializer.Deserialize<ManifestDto>(
                json,
                SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (dto is null ||
            !ReleaseVersion.TryParse(dto.Version, out var version) ||
            !Uri.TryCreate(dto.Setup, UriKind.Absolute, out var setupUri) ||
            !IsAllowedSetupScheme(setupUri) ||
            !IsValidSha256(dto.Sha256) ||
            dto.Size is null ||
            dto.Size <= 0 ||
            dto.Size > MaxSetupBytes)
        {
            return null;
        }

        Uri? notesUri = null;

        if (!string.IsNullOrWhiteSpace(dto.Notes))
        {
            if (!Uri.TryCreate(dto.Notes, UriKind.Absolute, out notesUri) ||
                !IsAllowedNotesScheme(notesUri))
            {
                return null;
            }
        }

        return new UpdateManifest(
            version,
            setupUri,
            dto.Sha256!,
            dto.Size.Value,
            notesUri);
    }

    public static bool IsValidSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isDigit = character is >= '0' and <= '9';
            var isLowerHex = character is >= 'a' and <= 'f';
            var isUpperHex = character is >= 'A' and <= 'F';

            if (!isDigit && !isLowerHex && !isUpperHex)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllowedSetupScheme(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps ||
        uri.Scheme == Uri.UriSchemeFile;

    private static bool IsAllowedNotesScheme(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps ||
        uri.Scheme == Uri.UriSchemeHttp;

    private sealed class ManifestDto
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("setup")]
        public string? Setup { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }

        [JsonPropertyName("size")]
        public long? Size { get; set; }

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }
    }
}

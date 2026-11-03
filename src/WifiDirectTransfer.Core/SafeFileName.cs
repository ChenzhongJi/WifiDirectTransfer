using System.Text;

namespace WifiDirectTransfer.Core;

/// <summary>
/// 接收端落盘前调用的文件名净化器。FileHeaderCodec 不调用此类型。
/// </summary>
public static class SafeFileName
{
    private static readonly char[] PathSeparators = { '\\', '/' };
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "CLOCK$",
        "CONIN$",
        "CONOUT$",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    };

    public static string Sanitize(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var separatorIndex = fileName.LastIndexOfAny(PathSeparators);
        var candidate = separatorIndex >= 0
            ? fileName[(separatorIndex + 1)..]
            : fileName;

        candidate = candidate.Trim();

        if (candidate.Length == 0)
        {
            return "file";
        }

        var builder = new StringBuilder(candidate.Length);

        for (var index = 0; index < candidate.Length; index++)
        {
            var character = candidate[index];

            if (char.IsHighSurrogate(character) &&
                index + 1 < candidate.Length &&
                char.IsLowSurrogate(candidate[index + 1]))
            {
                builder.Append(character);
                builder.Append(candidate[++index]);
                continue;
            }

            if (char.IsSurrogate(character) ||
                Array.IndexOf(InvalidFileNameChars, character) >= 0)
            {
                builder.Append('_');
                continue;
            }

            builder.Append(character);
        }

        candidate = builder.ToString();
        candidate = candidate.TrimEnd('.', ' ');

        if (candidate.Length == 0)
        {
            return "file";
        }

        while (candidate.Contains("..", StringComparison.Ordinal))
        {
            candidate = candidate.Replace("..", "_", StringComparison.Ordinal);
        }

        var firstDot = candidate.IndexOf('.');
        var baseName = firstDot >= 0 ? candidate[..firstDot] : candidate;

        if (ReservedNames.Contains(baseName))
        {
            candidate = "_" + candidate;
        }

        candidate = TruncateToUtf8ByteLength(candidate, FileTransferProtocol.MaxFileNameLength);
        candidate = candidate.TrimEnd('.', ' ');

        return candidate.Length == 0 ? "file" : candidate;
    }

    private static string TruncateToUtf8ByteLength(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var builder = new StringBuilder(maxBytes);
        var byteCount = 0;

        for (var index = 0; index < value.Length;)
        {
            var charCount = char.IsHighSurrogate(value[index]) &&
                            index + 1 < value.Length &&
                            char.IsLowSurrogate(value[index + 1])
                ? 2
                : 1;

            var span = value.AsSpan(index, charCount);
            var charByteCount = Encoding.UTF8.GetByteCount(span);

            if (byteCount + charByteCount > maxBytes)
            {
                break;
            }

            builder.Append(span);
            byteCount += charByteCount;
            index += charCount;
        }

        return builder.ToString();
    }
}

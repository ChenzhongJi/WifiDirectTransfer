using System.Text;

namespace WifiDirectTransfer.Core;

/// <summary>
/// 接收端将协议中的相对目录转换为安全的本地路径。
/// 拒绝绝对路径、盘符、UNC、父目录跳转，并逐段净化 Windows 文件名。
/// </summary>
public static class SafeRelativePath
{
    private static readonly char[] Separators = { '/', '\\' };
    private const int MaxSegments = 256;
    private const int MaxFullPathLength = 32767;

    /// <summary>
    /// 规范化相对目录，返回使用本地目录分隔符的路径。空字符串表示接收目录根。
    /// </summary>
    public static string Normalize(string? relativeDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativeDirectory))
        {
            return string.Empty;
        }

        var raw = relativeDirectory.Trim();
        raw = raw.Replace('\\', '/');

        if (raw.StartsWith('/') ||
            raw.StartsWith("//", StringComparison.Ordinal) ||
            (raw.Length >= 2 && char.IsLetter(raw[0]) && raw[1] == ':') ||
            raw.Contains(':'))
        {
            throw new InvalidFileHeaderException("相对目录不能是绝对路径、盘符或 UNC 路径。");
        }

        var segments = raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return string.Empty;
        }

        if (segments.Length > MaxSegments)
        {
            throw new InvalidFileHeaderException("相对目录层级过深。");
        }

        var sanitized = new string[segments.Length];

        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index].Trim();

            if (segment is "." or "..")
            {
                throw new InvalidFileHeaderException("相对目录不能包含父目录跳转。");
            }

            if (segment.Length == 0)
            {
                throw new InvalidFileHeaderException("相对目录包含空路径段。");
            }

            var safe = SafeFileName.Sanitize(segment);

            if (safe is "." or ".." || safe.Length == 0)
            {
                throw new InvalidFileHeaderException("相对目录包含无效路径段。");
            }

            sanitized[index] = safe;
        }

        var normalized = string.Join(Path.DirectorySeparatorChar, sanitized);
        var byteCount = Encoding.UTF8.GetByteCount(normalized);

        if (byteCount > FileTransferProtocol.MaxRelativeDirectoryLength)
        {
            throw new InvalidFileHeaderException(
                $"相对目录超过 {FileTransferProtocol.MaxRelativeDirectoryLength} 字节上限。");
        }

        return normalized;
    }

    /// <summary>
    /// 转为协议使用的正斜杠格式。
    /// </summary>
    public static string ToWire(string? relativeDirectory)
    {
        return Normalize(relativeDirectory).Replace(
            Path.DirectorySeparatorChar,
            '/');
    }

    /// <summary>
    /// 解析相对目录对应的本地目录，并确保结果仍位于接收根目录下。
    /// </summary>
    public static string ResolveDirectory(string rootDirectory, string? relativeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var root = Path.GetFullPath(rootDirectory);
        var relative = Normalize(relativeDirectory);
        var candidate = relative.Length == 0
            ? root
            : Path.GetFullPath(Path.Combine(root, relative));

        EnsureUnderRoot(root, candidate);
        return candidate;
    }

    /// <summary>
    /// 解析最终文件路径，文件名会经过 SafeFileName 净化。
    /// </summary>
    public static string ResolveFile(
        string rootDirectory,
        string? relativeDirectory,
        string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(fileName);

        var directory = ResolveDirectory(rootDirectory, relativeDirectory);
        var safeFileName = SafeFileName.Sanitize(fileName);

        if (safeFileName is "." or "..")
        {
            throw new InvalidFileHeaderException("文件名无效。");
        }

        var candidate = Path.GetFullPath(Path.Combine(directory, safeFileName));
        EnsureUnderRoot(Path.GetFullPath(rootDirectory), candidate);

        if (candidate.Length > MaxFullPathLength)
        {
            throw new InvalidFileHeaderException("文件路径过长。");
        }

        return candidate;
    }

    private static void EnsureUnderRoot(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);

        if (relative == ".." ||
            relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new InvalidFileHeaderException("路径越过了接收目录。");
        }
    }
}

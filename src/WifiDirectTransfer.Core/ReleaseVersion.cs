namespace WifiDirectTransfer.Core;

/// <summary>
/// 更新检查使用的版本号。只比较数字段，忽略 <c>+commit</c> 和 <c>-suffix</c>。
/// </summary>
public readonly struct ReleaseVersion :
    IComparable<ReleaseVersion>,
    IEquatable<ReleaseVersion>
{
    public ReleaseVersion(int major, int minor, int build, int revision = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(build);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);

        Major = major;
        Minor = minor;
        Build = build;
        Revision = revision;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Build { get; }

    public int Revision { get; }

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();

        // "1.6.0+abc123" 和 "1.6.0-preview" 只保留数字部分。
        var cutoff = value.IndexOfAny(new[] { '+', '-' });

        if (cutoff >= 0)
        {
            value = value[..cutoff];
        }

        var parts = value.Split('.');

        if (parts.Length is < 1 or > 4)
        {
            return false;
        }

        Span<int> numbers = stackalloc int[4];

        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];

            if (part.Length == 0)
            {
                return false;
            }

            foreach (var character in part)
            {
                if (character is < '0' or > '9')
                {
                    return false;
                }
            }

            if (!int.TryParse(part, out var number))
            {
                return false;
            }

            numbers[index] = number;
        }

        version = new ReleaseVersion(
            numbers[0],
            numbers[1],
            numbers[2],
            numbers[3]);
        return true;
    }

    public static ReleaseVersion Parse(string text)
    {
        if (!TryParse(text, out var version))
        {
            throw new FormatException($"版本号格式无效：{text}");
        }

        return version;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var result = Major.CompareTo(other.Major);

        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);

        if (result != 0)
        {
            return result;
        }

        result = Build.CompareTo(other.Build);

        return result != 0 ? result : Revision.CompareTo(other.Revision);
    }

    public bool Equals(ReleaseVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) =>
        obj is ReleaseVersion other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Major, Minor, Build, Revision);

    public override string ToString() =>
        Revision == 0
            ? $"{Major}.{Minor}.{Build}"
            : $"{Major}.{Minor}.{Build}.{Revision}";

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) =>
        left.CompareTo(right) > 0;

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) =>
        left.CompareTo(right) < 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) =>
        left.CompareTo(right) >= 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) =>
        left.CompareTo(right) <= 0;

    public static bool operator ==(ReleaseVersion left, ReleaseVersion right) =>
        left.Equals(right);

    public static bool operator !=(ReleaseVersion left, ReleaseVersion right) =>
        !left.Equals(right);
}

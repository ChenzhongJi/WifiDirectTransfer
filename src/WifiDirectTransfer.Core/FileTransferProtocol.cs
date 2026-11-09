namespace WifiDirectTransfer.Core;

/// <summary>
/// 文件头与块传输的基础参数，M0/M1 共用。
/// </summary>
public static class FileTransferProtocol
{
    private static readonly byte[] MagicBytesValue = { 0x57, 0x46, 0x44, 0x31 };

    /// <summary>Magic "WFD1" 的原始字节。</summary>
    public static ReadOnlySpan<byte> Magic => MagicBytesValue;

    /// <summary>Magic 按 little-endian uint 读取时的值。</summary>
    public const uint MagicValue = 0x31444657;

    public const byte Version = 2;

    public const int FixedHeaderLength = 24;

    public const int MaxFileNameLength = 255;

    /// <summary>相对目录的最大 UTF-8 字节长度。空字符串表示扁平文件。</summary>
    public const int MaxRelativeDirectoryLength = 8 * 1024;

    /// <summary>默认最大文件大小 2 GiB，可在编解码器和传输 API 中覆盖。</summary>
    public const long DefaultMaxFileSize = 2L * 1024 * 1024 * 1024;

    public const int BlockSize = 64 * 1024;
}

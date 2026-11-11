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

    /// <summary>1.1.x 及更早版本的单文件上限。</summary>
    public const long LegacyMaxFileSize = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// 默认最大文件大小 16 GiB，可在编解码器和传输 API 中覆盖。
    /// 超过 2 GiB 的文件要求两端都使用 1.2 或更高版本。
    /// </summary>
    public const long DefaultMaxFileSize = 16L * 1024 * 1024 * 1024;

    public const int BlockSize = 64 * 1024;

    /// <summary>1.5+ 窗口模式的默认在途块数。</summary>
    public const int DefaultWindowSize = 64;

    /// <summary>1.5+ 窗口模式下每多少个块发送一次累计 ACK。</summary>
    public const int DefaultWindowAckInterval = 8;

    /// <summary>窗口模式下每多少个块 Flush 一次接收文件。</summary>
    public const int DefaultWindowFlushIntervalBlocks = 32;

    /// <summary>窗口模式和累计 ACK 的最大可协商值。</summary>
    public const int MaxWindowSize = 64;
}

namespace WifiDirectTransfer.Core;

/// <summary>
/// 24 字节定长文件头。文件名本身不在此对象中，由发送端/接收端按 FileNameLength 单独处理。
/// </summary>
public sealed record FileHeader
{
    public uint Magic { get; init; } = FileTransferProtocol.MagicValue;

    public byte Version { get; init; } = FileTransferProtocol.Version;

    public MessageType MessageType { get; init; } = MessageType.FileStart;

    public ushort HeaderLength { get; init; }

    public ushort FileNameLength { get; init; }

    public long FileSize { get; init; }

    public ChecksumAlgorithm ChecksumAlgorithm { get; init; } = ChecksumAlgorithm.Sha256;

    public FileFlags Flags { get; init; }

    public uint Reserved { get; init; }

    public static FileHeader CreateFileStart(
        long fileSize,
        int fileNameByteLength,
        FileFlags flags = FileFlags.LastFile)
    {
        if (fileSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileSize), "文件大小不能为负数。");
        }

        if (fileNameByteLength is < 1 or > FileTransferProtocol.MaxFileNameLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fileNameByteLength),
                $"文件名字节长度必须在 1..{FileTransferProtocol.MaxFileNameLength} 之间。");
        }

        if ((flags & ~FileFlags.LastFile) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(flags), "FileFlags 包含未知标志。");
        }

        return new FileHeader
        {
            HeaderLength = (ushort)(FileTransferProtocol.FixedHeaderLength + fileNameByteLength),
            FileNameLength = (ushort)fileNameByteLength,
            FileSize = fileSize,
            ChecksumAlgorithm = ChecksumAlgorithm.Sha256,
            Flags = flags,
            Reserved = 0
        };
    }
}

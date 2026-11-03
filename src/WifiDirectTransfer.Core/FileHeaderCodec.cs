using System.Buffers.Binary;
using System.Text;

namespace WifiDirectTransfer.Core;

/// <summary>
/// 负责 24 字节定长前缀的序列化和校验，以及文件名的严格 UTF-8 编解码。
/// 这里不做任何文件系统名称净化，SafeFileName 由接收端在落盘前调用。
/// </summary>
public sealed class FileHeaderCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly long _maxFileSize;

    public FileHeaderCodec(long maxFileSize = FileTransferProtocol.DefaultMaxFileSize)
    {
        if (maxFileSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileSize), "最大文件大小必须大于零。");
        }

        _maxFileSize = maxFileSize;
    }

    public long MaxFileSize => _maxFileSize;

    public byte[] Serialize(FileHeader header)
    {
        var buffer = new byte[FileTransferProtocol.FixedHeaderLength];
        Serialize(header, buffer);
        return buffer;
    }

    public void Serialize(FileHeader header, Span<byte> destination)
    {
        if (destination.Length < FileTransferProtocol.FixedHeaderLength)
        {
            throw new ArgumentException(
                $"目标缓冲区至少需要 {FileTransferProtocol.FixedHeaderLength} 字节。",
                nameof(destination));
        }

        Validate(header);

        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[..4],
            header.Magic);
        destination[4] = header.Version;
        destination[5] = (byte)header.MessageType;
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination.Slice(6, 2),
            header.HeaderLength);
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination.Slice(8, 2),
            header.FileNameLength);
        BinaryPrimitives.WriteInt64LittleEndian(
            destination.Slice(10, 8),
            header.FileSize);
        destination[18] = (byte)header.ChecksumAlgorithm;
        destination[19] = (byte)header.Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination.Slice(20, 4),
            header.Reserved);
    }

    public FileHeader Deserialize(ReadOnlySpan<byte> headerBytes)
    {
        if (headerBytes.Length < FileTransferProtocol.FixedHeaderLength)
        {
            throw new InvalidFileHeaderException("文件头长度不足 24 字节。");
        }

        var header = new FileHeader
        {
            Magic = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[..4]),
            Version = headerBytes[4],
            MessageType = (MessageType)headerBytes[5],
            HeaderLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.Slice(6, 2)),
            FileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.Slice(8, 2)),
            FileSize = BinaryPrimitives.ReadInt64LittleEndian(headerBytes.Slice(10, 8)),
            ChecksumAlgorithm = (ChecksumAlgorithm)headerBytes[18],
            Flags = (FileFlags)headerBytes[19],
            Reserved = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.Slice(20, 4))
        };

        Validate(header);
        return header;
    }

    /// <summary>
    /// 序列化 M1 FileStart payload：24 字节 FileHeader + UTF-8 文件名。
    /// </summary>
    public byte[] SerializeFileStartPayload(FileHeader header, string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return SerializeFileStartPayload(header, EncodeFileName(fileName));
    }

    /// <summary>
    /// 序列化 M1 FileStart payload：24 字节 FileHeader + 已编码的 UTF-8 文件名。
    /// </summary>
    public byte[] SerializeFileStartPayload(
        FileHeader header,
        ReadOnlySpan<byte> fileNameBytes)
    {
        ValidateFileNameLength(fileNameBytes.Length);

        if (header.FileNameLength != fileNameBytes.Length)
        {
            throw new InvalidFileHeaderException(
                "FileHeader.FileNameLength 与实际文件名长度不一致。");
        }

        var headerBytes = Serialize(header);
        var payload = new byte[FileTransferProtocol.FixedHeaderLength + fileNameBytes.Length];

        headerBytes.CopyTo(payload, 0);
        fileNameBytes.CopyTo(payload.AsSpan(FileTransferProtocol.FixedHeaderLength));

        return payload;
    }

    /// <summary>
    /// 反序列化 M1 FileStart payload。返回的 FileName 尚未经过 SafeFileName。
    /// </summary>
    public FileStartPayload DeserializeFileStartPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FileTransferProtocol.FixedHeaderLength)
        {
            throw new InvalidFileHeaderException("FileStart payload 长度不足 24 字节。");
        }

        var header = Deserialize(payload[..FileTransferProtocol.FixedHeaderLength]);

        var expectedLength = FileTransferProtocol.FixedHeaderLength + header.FileNameLength;

        if (payload.Length != expectedLength)
        {
            throw new InvalidFileHeaderException(
                $"FileStart payload 长度必须为 {expectedLength} 字节。");
        }

        var fileName = DecodeFileName(
            payload.Slice(FileTransferProtocol.FixedHeaderLength, header.FileNameLength));

        return new FileStartPayload(header, fileName);
    }

    public static byte[] EncodeFileName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        byte[] bytes;

        try
        {
            bytes = StrictUtf8.GetBytes(fileName);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidFileHeaderException("文件名包含无法编码的 Unicode 字符。", exception);
        }

        ValidateFileNameLength(bytes.Length);
        return bytes;
    }

    public static string DecodeFileName(ReadOnlySpan<byte> fileNameBytes)
    {
        ValidateFileNameLength(fileNameBytes.Length);

        try
        {
            return StrictUtf8.GetString(fileNameBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidFileHeaderException("文件名不是有效的 UTF-8 编码。", exception);
        }
    }

    private static void ValidateFileNameLength(int fileNameByteLength)
    {
        if (fileNameByteLength is < 1 or > FileTransferProtocol.MaxFileNameLength)
        {
            throw new InvalidFileHeaderException(
                $"文件名字节长度必须在 1..{FileTransferProtocol.MaxFileNameLength} 之间。");
        }
    }

    private void Validate(FileHeader header)
    {
        if (header.Magic != FileTransferProtocol.MagicValue)
        {
            throw new InvalidFileHeaderException("文件头 Magic 必须为 WFD1。");
        }

        if (header.Version != FileTransferProtocol.Version)
        {
            throw new InvalidFileHeaderException("不支持的文件协议版本。");
        }

        if (header.MessageType != MessageType.FileStart)
        {
            throw new InvalidFileHeaderException("文件头 MessageType 必须为 FileStart。");
        }

        ValidateFileNameLength(header.FileNameLength);

        if (header.HeaderLength != FileTransferProtocol.FixedHeaderLength + header.FileNameLength)
        {
            throw new InvalidFileHeaderException(
                "HeaderLength 必须等于 24 加 FileNameLength。");
        }

        if (header.FileSize < 0)
        {
            throw new InvalidFileHeaderException("FileSize 不能为负数。");
        }

        if (header.FileSize > _maxFileSize)
        {
            throw new InvalidFileHeaderException(
                $"FileSize 超过允许的最大值 {_maxFileSize} 字节。");
        }

        if (header.ChecksumAlgorithm != ChecksumAlgorithm.Sha256)
        {
            throw new InvalidFileHeaderException("仅接受 SHA-256 校验算法。");
        }

        if ((header.Flags & ~FileFlags.LastFile) != 0)
        {
            throw new InvalidFileHeaderException("FileFlags 只允许包含 LastFile。");
        }

        if (header.Reserved != 0)
        {
            throw new InvalidFileHeaderException("Reserved 字段必须全为零。");
        }
    }
}

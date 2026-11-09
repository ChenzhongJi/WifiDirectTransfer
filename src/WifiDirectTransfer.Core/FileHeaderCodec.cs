using System.Buffers.Binary;
using System.Text;

namespace WifiDirectTransfer.Core;

/// <summary>
/// 负责 24 字节定长前缀的序列化和校验，以及文件名和相对目录的严格 UTF-8 编解码。
/// 这里不做任何文件系统路径净化，SafeFileName / SafeRelativePath 由接收端在落盘前调用。
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
    /// 序列化 FileStart payload：24 字节 FileHeader + UTF-8 文件名 + UTF-8 相对目录。
    /// 相对目录为空时保持扁平文件格式。
    /// </summary>
    public byte[] SerializeFileStartPayload(
        FileHeader header,
        string fileName,
        string relativeDirectory = "")
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(relativeDirectory);

        return SerializeFileStartPayload(
            header,
            EncodeFileName(fileName),
            EncodeRelativeDirectory(relativeDirectory));
    }

    /// <summary>
    /// 序列化扁平 FileStart payload，文件名称已编码为 UTF-8。
    /// </summary>
    public byte[] SerializeFileStartPayload(
        FileHeader header,
        ReadOnlySpan<byte> fileNameBytes)
    {
        return SerializeFileStartPayload(
            header,
            fileNameBytes,
            ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// 序列化 FileStart payload，文件名称和相对目录已编码为 UTF-8。
    /// </summary>
    public byte[] SerializeFileStartPayload(
        FileHeader header,
        ReadOnlySpan<byte> fileNameBytes,
        ReadOnlySpan<byte> relativeDirectoryBytes)
    {
        ValidateFileNameLength(fileNameBytes.Length);
        ValidateRelativeDirectoryLength(relativeDirectoryBytes.Length);

        if (header.FileNameLength != fileNameBytes.Length)
        {
            throw new InvalidFileHeaderException(
                "FileHeader.FileNameLength 与实际文件名长度不一致。");
        }

        var expectedHeaderLength = FileTransferProtocol.FixedHeaderLength +
                                   fileNameBytes.Length +
                                   relativeDirectoryBytes.Length;

        if (header.HeaderLength != expectedHeaderLength)
        {
            throw new InvalidFileHeaderException(
                "FileHeader.HeaderLength 与文件名、相对目录长度不一致。");
        }

        var headerBytes = Serialize(header);
        var payload = new byte[expectedHeaderLength];

        headerBytes.CopyTo(payload, 0);
        fileNameBytes.CopyTo(payload.AsSpan(FileTransferProtocol.FixedHeaderLength));
        relativeDirectoryBytes.CopyTo(
            payload.AsSpan(FileTransferProtocol.FixedHeaderLength + fileNameBytes.Length));

        return payload;
    }

    /// <summary>
    /// 反序列化 FileStart payload。返回的文件名和相对目录尚未经过路径净化。
    /// </summary>
    public FileStartPayload DeserializeFileStartPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FileTransferProtocol.FixedHeaderLength)
        {
            throw new InvalidFileHeaderException("FileStart payload 长度不足 24 字节。");
        }

        var header = Deserialize(payload[..FileTransferProtocol.FixedHeaderLength]);
        var relativeDirectoryLength = header.HeaderLength -
                                      FileTransferProtocol.FixedHeaderLength -
                                      header.FileNameLength;

        ValidateRelativeDirectoryLength(relativeDirectoryLength);

        if (payload.Length != header.HeaderLength)
        {
            throw new InvalidFileHeaderException(
                $"FileStart payload 长度必须为 {header.HeaderLength} 字节。");
        }

        var fileName = DecodeFileName(
            payload.Slice(FileTransferProtocol.FixedHeaderLength, header.FileNameLength));
        var relativeDirectory = DecodeRelativeDirectory(
            payload.Slice(
                FileTransferProtocol.FixedHeaderLength + header.FileNameLength,
                relativeDirectoryLength));

        return new FileStartPayload(header, fileName, relativeDirectory);
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

    public static byte[] EncodeRelativeDirectory(string relativeDirectory)
    {
        ArgumentNullException.ThrowIfNull(relativeDirectory);

        if (relativeDirectory.Length == 0)
        {
            return Array.Empty<byte>();
        }

        byte[] bytes;

        try
        {
            bytes = StrictUtf8.GetBytes(relativeDirectory);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidFileHeaderException(
                "相对目录包含无法编码的 Unicode 字符。",
                exception);
        }

        ValidateRelativeDirectoryLength(bytes.Length);
        return bytes;
    }

    public static string DecodeRelativeDirectory(ReadOnlySpan<byte> relativeDirectoryBytes)
    {
        if (relativeDirectoryBytes.Length == 0)
        {
            return string.Empty;
        }

        ValidateRelativeDirectoryLength(relativeDirectoryBytes.Length);

        try
        {
            return StrictUtf8.GetString(relativeDirectoryBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidFileHeaderException(
                "相对目录不是有效的 UTF-8 编码。",
                exception);
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

    private static void ValidateRelativeDirectoryLength(int relativeDirectoryByteLength)
    {
        if (relativeDirectoryByteLength is < 0 or > FileTransferProtocol.MaxRelativeDirectoryLength)
        {
            throw new InvalidFileHeaderException(
                $"相对目录字节长度必须在 0..{FileTransferProtocol.MaxRelativeDirectoryLength} 之间。");
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

        if (header.HeaderLength <
            FileTransferProtocol.FixedHeaderLength + header.FileNameLength)
        {
            throw new InvalidFileHeaderException(
                "HeaderLength 不能小于 24 加 FileNameLength。");
        }

        var relativeDirectoryLength = header.HeaderLength -
                                      FileTransferProtocol.FixedHeaderLength -
                                      header.FileNameLength;

        ValidateRelativeDirectoryLength(relativeDirectoryLength);

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

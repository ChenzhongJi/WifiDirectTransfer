using System.Buffers.Binary;
using System.Text;

namespace WifiDirectTransfer.Core;

public sealed record TransferMetadata(
    string FileName,
    long FileSize,
    long PersistedBytes);

/// <summary>
/// .meta 断点文件格式：magic(8) + version(4) + nameLength(4) + fileName + fileSize(8) + persistedBytes(8)。
/// 所有多字节字段使用小端。
/// </summary>
public static class TransferMetadataCodec
{
    public const int MagicLength = 8;
    public const int Version = 1;
    public const int HeaderLength = MagicLength + 4 + 4;
    public const int TrailerLength = 8 + 8;

    private static readonly byte[] Magic =
    {
        0x57, 0x46, 0x44, 0x31, 0x4D, 0x45, 0x54, 0x41
    };

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static byte[] Serialize(TransferMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (metadata.FileSize < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(metadata),
                "文件大小不能为负数。");
        }

        if (metadata.PersistedBytes < 0 ||
            metadata.PersistedBytes > metadata.FileSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(metadata),
                "已持久化字节数无效。");
        }

        var fileNameBytes = FileHeaderCodec.EncodeFileName(metadata.FileName);
        var bytes = new byte[
            HeaderLength + fileNameBytes.Length + TrailerLength];

        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(MagicLength, 4),
            Version);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(MagicLength + 4, 4),
            fileNameBytes.Length);
        fileNameBytes.CopyTo(bytes, HeaderLength);
        BinaryPrimitives.WriteInt64LittleEndian(
            bytes.AsSpan(HeaderLength + fileNameBytes.Length, 8),
            metadata.FileSize);
        BinaryPrimitives.WriteInt64LittleEndian(
            bytes.AsSpan(HeaderLength + fileNameBytes.Length + 8, 8),
            metadata.PersistedBytes);

        return bytes;
    }

    public static bool TryDeserialize(
        ReadOnlySpan<byte> bytes,
        out TransferMetadata? metadata)
    {
        metadata = null;

        if (bytes.Length < HeaderLength + TrailerLength)
        {
            return false;
        }

        if (!bytes[..MagicLength].SequenceEqual(Magic))
        {
            return false;
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(
            bytes.Slice(MagicLength, 4));

        if (version != Version)
        {
            return false;
        }

        var nameLength = BinaryPrimitives.ReadInt32LittleEndian(
            bytes.Slice(MagicLength + 4, 4));

        if (nameLength is < 1 or > FileTransferProtocol.MaxFileNameLength)
        {
            return false;
        }

        if (bytes.Length != HeaderLength + nameLength + TrailerLength)
        {
            return false;
        }

        string fileName;

        try
        {
            fileName = StrictUtf8.GetString(
                bytes.Slice(HeaderLength, nameLength));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        var fileSize = BinaryPrimitives.ReadInt64LittleEndian(
            bytes.Slice(HeaderLength + nameLength, 8));
        var persistedBytes = BinaryPrimitives.ReadInt64LittleEndian(
            bytes.Slice(HeaderLength + nameLength + 8, 8));

        if (fileSize < 0 ||
            persistedBytes < 0 ||
            persistedBytes > fileSize)
        {
            return false;
        }

        metadata = new TransferMetadata(
            fileName,
            fileSize,
            persistedBytes);
        return true;
    }
}

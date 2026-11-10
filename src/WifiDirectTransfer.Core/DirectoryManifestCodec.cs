using System.Buffers.Binary;
using System.Text;

namespace WifiDirectTransfer.Core;

/// <summary>
/// 1.3+ 目录清单扩展。每个消息独立携带一组需要创建的空文件夹，
/// 接收端可以按任意顺序处理，重复创建是幂等的。
/// payload: subtype(1) + version(1) + entryCount(4) + entries。
/// entry: length(2) + UTF-8 relative directory。
/// </summary>
public static class DirectoryManifestCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public const byte Version = 1;

    public const int HeaderLength = 1 + 1 + 4;

    public const int MaxEntriesPerMessage = 65_535;

    public const int MaxPayloadLength =
        MessageProtocol.DefaultMaxPayloadLength;

    /// <summary>单次连接最多允许创建的空文件夹数量，防止恶意清单。</summary>
    public const int MaxDirectoriesPerTransfer = 100_000;

    public static byte[] Serialize(IReadOnlyList<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);

        if (directories.Count == 0)
        {
            throw new MessageProtocolException(
                "目录清单至少需要一个条目。");
        }

        if (directories.Count > MaxEntriesPerMessage)
        {
            throw new MessageProtocolException(
                $"目录清单条目数超过上限 {MaxEntriesPerMessage}。");
        }

        var entryBytes = new byte[directories.Count][];
        var payloadLength = HeaderLength;

        for (var index = 0; index < directories.Count; index++)
        {
            entryBytes[index] = EncodeEntry(directories[index]);
            payloadLength += 2 + entryBytes[index].Length;
        }

        if (payloadLength > MaxPayloadLength)
        {
            throw new MessageProtocolException(
                $"目录清单 payload 超过 {MaxPayloadLength} 字节上限。");
        }

        var payload = new byte[payloadLength];
        payload[0] = (byte)ControlMessageSubtype.DirectoryManifest;
        payload[1] = Version;
        BinaryPrimitives.WriteInt32BigEndian(
            payload.AsSpan(2, 4),
            directories.Count);

        var offset = HeaderLength;

        foreach (var bytes in entryBytes)
        {
            BinaryPrimitives.WriteUInt16BigEndian(
                payload.AsSpan(offset, 2),
                (ushort)bytes.Length);
            bytes.CopyTo(payload, offset + 2);
            offset += 2 + bytes.Length;
        }

        return payload;
    }

    public static IReadOnlyList<string> Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderLength)
        {
            throw new MessageProtocolException(
                "DirectoryManifest payload 长度不足。");
        }

        if (payload.Length > MaxPayloadLength)
        {
            throw new MessageProtocolException(
                $"DirectoryManifest payload 超过 {MaxPayloadLength} 字节上限。");
        }

        if (payload[0] != (byte)ControlMessageSubtype.DirectoryManifest)
        {
            throw new MessageProtocolException(
                "DirectoryManifest 子类型不正确。");
        }

        if (payload[1] != Version)
        {
            throw new MessageProtocolException(
                "不支持的 DirectoryManifest 版本。");
        }

        var count = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(2, 4));

        if (count is < 1 or > MaxEntriesPerMessage)
        {
            throw new MessageProtocolException(
                "DirectoryManifest 条目数无效。");
        }

        var directories = new List<string>(count);
        var offset = HeaderLength;

        for (var index = 0; index < count; index++)
        {
            if (payload.Length - offset < 2)
            {
                throw new MessageProtocolException(
                    "DirectoryManifest 条目长度字段缺失。");
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(
                payload.Slice(offset, 2));
            offset += 2;

            if (length is < 1 or > FileTransferProtocol.MaxRelativeDirectoryLength)
            {
                throw new MessageProtocolException(
                    "DirectoryManifest 条目长度无效。");
            }

            if (payload.Length - offset < length)
            {
                throw new MessageProtocolException(
                    "DirectoryManifest 条目数据不完整。");
            }

            try
            {
                directories.Add(
                    StrictUtf8.GetString(payload.Slice(offset, length)));
            }
            catch (DecoderFallbackException exception)
            {
                throw new MessageProtocolException(
                    "DirectoryManifest 条目不是有效的 UTF-8。",
                    exception);
            }

            offset += length;
        }

        if (offset != payload.Length)
        {
            throw new MessageProtocolException(
                "DirectoryManifest payload 包含多余数据。");
        }

        return directories;
    }

    /// <summary>编码单个目录条目并返回 UTF-8 字节，同时校验长度。</summary>
    public static byte[] EncodeEntry(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (directory.Length == 0)
        {
            throw new MessageProtocolException(
                "目录清单条目不能为空。");
        }

        byte[] bytes;

        try
        {
            bytes = StrictUtf8.GetBytes(directory);
        }
        catch (EncoderFallbackException exception)
        {
            throw new MessageProtocolException(
                "目录清单条目不是有效的 Unicode。",
                exception);
        }

        if (bytes.Length is < 1 or > FileTransferProtocol.MaxRelativeDirectoryLength)
        {
            throw new MessageProtocolException(
                $"目录清单条目超过 {FileTransferProtocol.MaxRelativeDirectoryLength} 字节上限。");
        }

        return bytes;
    }

    /// <summary>估算单个条目的编码长度，供发送端拆分消息。</summary>
    public static int GetEncodedEntryLength(string directory) =>
        2 + EncodeEntry(directory).Length;
}

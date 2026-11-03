using System.Buffers.Binary;
using System.Text;

namespace WifiDirectTransfer.Core;

public sealed record ControlMessage(
    ControlMessageSubtype Subtype,
    long Offset,
    WifiDirectErrorCode ErrorCode,
    string Message,
    byte[]? Digest = null,
    int ProtocolVersion = 0,
    int MaxPayloadLength = 0);

/// <summary>
/// ControlMessage payload 编解码：subtype(1) + data。多字节字段使用大端。
/// </summary>
public static class ControlMessageCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public const int OffsetPayloadLength = 1 + 8;
    public const int TransferErrorHeaderLength = 1 + 4;
    public const int FileVerifiedPayloadLength =
        1 + MessageProtocol.FileEndDigestLength;
    public const int HelloHeaderLength = 1 + 4 + 4;

    public static byte[] SerializeAck(long persistedOffset)
    {
        ValidateOffset(persistedOffset);

        var payload = new byte[OffsetPayloadLength];
        payload[0] = (byte)ControlMessageSubtype.Ack;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1, 8), persistedOffset);
        return payload;
    }

    public static byte[] SerializeResumeRequest(long offset)
    {
        ValidateOffset(offset);

        var payload = new byte[OffsetPayloadLength];
        payload[0] = (byte)ControlMessageSubtype.ResumeRequest;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1, 8), offset);
        return payload;
    }

    /// <summary>
    /// 队列总字节数。接收端只在队列第一个 FileStart 上使用该值做空间预检。
    /// 发送 0 表示清除之前的队列信息。
    /// </summary>
    public static byte[] SerializeQueueInfo(long totalBytes)
    {
        ValidateOffset(totalBytes);

        var payload = new byte[OffsetPayloadLength];
        payload[0] = (byte)ControlMessageSubtype.QueueInfo;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1, 8), totalBytes);
        return payload;
    }

    public static byte[] SerializeFileSkipped()
    {
        return new[] { (byte)ControlMessageSubtype.FileSkipped };
    }

    /// <summary>
    /// 接收端在 SHA-256 校验通过且正式文件落盘后发送，携带接收端计算的摘要。
    /// </summary>
    public static byte[] SerializeFileVerified(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != MessageProtocol.FileEndDigestLength)
        {
            throw new MessageProtocolException(
                $"FileVerified 摘要必须为 {MessageProtocol.FileEndDigestLength} 字节。");
        }

        var payload = new byte[FileVerifiedPayloadLength];
        payload[0] = (byte)ControlMessageSubtype.FileVerified;
        digest.CopyTo(payload.AsSpan(1));
        return payload;
    }

    /// <summary>
    /// 连接建立后交换协议版本、单帧上限和构建号。
    /// </summary>
    public static byte[] SerializeHello(
        int protocolVersion,
        int maxPayloadLength,
        string build)
    {
        ArgumentNullException.ThrowIfNull(build);

        if (protocolVersion <= 0)
        {
            throw new MessageProtocolException("协议版本必须大于零。");
        }

        if (maxPayloadLength <= 0)
        {
            throw new MessageProtocolException("单帧上限必须大于零。");
        }

        byte[] buildBytes;

        try
        {
            buildBytes = StrictUtf8.GetBytes(build);
        }
        catch (EncoderFallbackException exception)
        {
            throw new MessageProtocolException("构建号不是有效的 Unicode。", exception);
        }

        var payload = new byte[HelloHeaderLength + buildBytes.Length];
        payload[0] = (byte)ControlMessageSubtype.Hello;
        BinaryPrimitives.WriteInt32BigEndian(
            payload.AsSpan(1, 4),
            protocolVersion);
        BinaryPrimitives.WriteInt32BigEndian(
            payload.AsSpan(5, 4),
            maxPayloadLength);
        buildBytes.CopyTo(payload, HelloHeaderLength);

        return payload;
    }

    public static byte[] SerializeTransferError(
        WifiDirectErrorCode errorCode,
        string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        byte[] messageBytes;

        try
        {
            messageBytes = StrictUtf8.GetBytes(message);
        }
        catch (EncoderFallbackException exception)
        {
            throw new MessageProtocolException("传输错误消息不是有效的 Unicode。", exception);
        }

        var payload = new byte[TransferErrorHeaderLength + messageBytes.Length];
        payload[0] = (byte)ControlMessageSubtype.TransferError;
        BinaryPrimitives.WriteInt32BigEndian(
            payload.AsSpan(1, 4),
            (int)errorCode);
        messageBytes.CopyTo(payload, TransferErrorHeaderLength);

        return payload;
    }

    public static ControlMessage Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
        {
            throw new MessageProtocolException("ControlMessage payload 缺少子类型。");
        }

        var subtype = (ControlMessageSubtype)payload[0];

        return subtype switch
        {
            ControlMessageSubtype.Ack => DeserializeOffset(payload, subtype),
            ControlMessageSubtype.ResumeRequest => DeserializeOffset(payload, subtype),
            ControlMessageSubtype.QueueInfo => DeserializeOffset(payload, subtype),
            ControlMessageSubtype.TransferError => DeserializeTransferError(payload),
            ControlMessageSubtype.FileSkipped => DeserializeFileSkipped(payload),
            ControlMessageSubtype.FileVerified => DeserializeFileVerified(payload),
            ControlMessageSubtype.Hello => DeserializeHello(payload),
            _ => throw new MessageProtocolException(
                $"未知的 ControlMessage 子类型：0x{payload[0]:X2}。")
        };
    }

    private static ControlMessage DeserializeOffset(
        ReadOnlySpan<byte> payload,
        ControlMessageSubtype subtype)
    {
        if (payload.Length != OffsetPayloadLength)
        {
            throw new MessageProtocolException(
                $"{subtype} payload 长度必须为 {OffsetPayloadLength} 字节。");
        }

        var offset = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(1, 8));
        ValidateOffset(offset);

        return new ControlMessage(subtype, offset, default, string.Empty);
    }

    private static ControlMessage DeserializeFileSkipped(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1)
        {
            throw new MessageProtocolException(
                "FileSkipped payload 长度必须为 1 字节。");
        }

        return new ControlMessage(
            ControlMessageSubtype.FileSkipped,
            0,
            WifiDirectErrorCode.FileSkipped,
            string.Empty);
    }

    private static ControlMessage DeserializeFileVerified(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != FileVerifiedPayloadLength)
        {
            throw new MessageProtocolException(
                $"FileVerified payload 长度必须为 {FileVerifiedPayloadLength} 字节。");
        }

        return new ControlMessage(
            ControlMessageSubtype.FileVerified,
            0,
            default,
            string.Empty,
            payload[1..].ToArray());
    }

    private static ControlMessage DeserializeHello(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HelloHeaderLength)
        {
            throw new MessageProtocolException(
                "Hello payload 缺少版本或单帧上限。");
        }

        var protocolVersion = BinaryPrimitives.ReadInt32BigEndian(
            payload.Slice(1, 4));
        var maxPayloadLength = BinaryPrimitives.ReadInt32BigEndian(
            payload.Slice(5, 4));

        if (protocolVersion <= 0 || maxPayloadLength <= 0)
        {
            throw new MessageProtocolException(
                "Hello 的协议版本或单帧上限无效。");
        }

        string build;

        try
        {
            build = StrictUtf8.GetString(payload[HelloHeaderLength..]);
        }
        catch (DecoderFallbackException exception)
        {
            throw new MessageProtocolException(
                "Hello 构建号不是有效的 UTF-8。",
                exception);
        }

        if (build.Length > 200)
        {
            throw new MessageProtocolException("Hello 构建号过长。");
        }

        return new ControlMessage(
            ControlMessageSubtype.Hello,
            0,
            default,
            build,
            null,
            protocolVersion,
            maxPayloadLength);
    }

    private static ControlMessage DeserializeTransferError(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < TransferErrorHeaderLength)
        {
            throw new MessageProtocolException(
                "TransferError payload 缺少错误码。");
        }

        var errorCode = (WifiDirectErrorCode)BinaryPrimitives.ReadInt32BigEndian(
            payload.Slice(1, 4));

        string message;

        try
        {
            message = StrictUtf8.GetString(payload[TransferErrorHeaderLength..]);
        }
        catch (DecoderFallbackException exception)
        {
            throw new MessageProtocolException(
                "TransferError 消息不是有效的 UTF-8。",
                exception);
        }

        return new ControlMessage(
            ControlMessageSubtype.TransferError,
            0,
            errorCode,
            message);
    }

    private static void ValidateOffset(long offset)
    {
        if (offset < 0)
        {
            throw new MessageProtocolException("控制消息偏移不能为负数。");
        }
    }
}

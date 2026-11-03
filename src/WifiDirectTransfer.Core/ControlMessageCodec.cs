using System.Buffers.Binary;
using System.Text;

namespace WifiDirectTransfer.Core;

public sealed record ControlMessage(
    ControlMessageSubtype Subtype,
    long Offset,
    WifiDirectErrorCode ErrorCode,
    string Message);

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

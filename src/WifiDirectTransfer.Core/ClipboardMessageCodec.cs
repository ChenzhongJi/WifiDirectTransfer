using System.Text;

namespace WifiDirectTransfer.Core;

public enum ClipboardContentType : byte
{
    Text = 0,
    Image = 1
}

public sealed class ClipboardContent
{
    public ClipboardContent(ClipboardContentType type, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Type = type;
        Data = data;
    }

    public ClipboardContentType Type { get; }

    /// <summary>
    /// 文本为 UTF-8 字节，图片为 PNG 字节。
    /// </summary>
    public byte[] Data { get; }
}

/// <summary>
/// ClipboardUpdate 帧 payload：1 字节内容类型 + 实际数据。
/// </summary>
public static class ClipboardMessageCodec
{
    public const int MaxImageBytes = 32 * 1024 * 1024;
    public const int MaxPayloadLength = MaxImageBytes + 1;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static byte[] EncodeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        byte[] bytes;

        try
        {
            bytes = StrictUtf8.GetBytes(text);
        }
        catch (EncoderFallbackException exception)
        {
            throw new MessageProtocolException(
                "剪贴板文本不是有效的 Unicode。",
                exception);
        }

        return Encode(ClipboardContentType.Text, bytes);
    }

    public static byte[] EncodeImage(ReadOnlySpan<byte> pngBytes)
    {
        if (pngBytes.Length == 0)
        {
            throw new MessageProtocolException("剪贴板图片为空。");
        }

        if (pngBytes.Length > MaxImageBytes)
        {
            throw new MessageProtocolException(
                $"剪贴板图片超过 {MaxImageBytes} 字节限制。");
        }

        return Encode(ClipboardContentType.Image, pngBytes);
    }

    public static byte[] Encode(
        ClipboardContentType type,
        ReadOnlySpan<byte> data)
    {
        if (!Enum.IsDefined(type))
        {
            throw new MessageProtocolException(
                $"未知的剪贴板内容类型：0x{(byte)type:X2}。");
        }

        if (data.Length > MaxPayloadLength - 1)
        {
            throw new MessageProtocolException(
                $"剪贴板内容超过 {MaxPayloadLength - 1} 字节限制。");
        }

        var payload = new byte[1 + data.Length];
        payload[0] = (byte)type;
        data.CopyTo(payload.AsSpan(1));
        return payload;
    }

    public static ClipboardContent Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
        {
            throw new MessageProtocolException(
                "ClipboardUpdate payload 缺少内容类型。");
        }

        var type = (ClipboardContentType)payload[0];

        if (!Enum.IsDefined(type))
        {
            throw new MessageProtocolException(
                $"未知的剪贴板内容类型：0x{payload[0]:X2}。");
        }

        var data = payload[1..].ToArray();

        if (type == ClipboardContentType.Image &&
            data.Length > MaxImageBytes)
        {
            throw new MessageProtocolException(
                $"剪贴板图片超过 {MaxImageBytes} 字节限制。");
        }

        return new ClipboardContent(type, data);
    }

    public static string DecodeText(ReadOnlySpan<byte> data)
    {
        try
        {
            return StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException exception)
        {
            throw new MessageProtocolException(
                "剪贴板文本不是有效的 UTF-8。",
                exception);
        }
    }
}

using System.Text;

namespace WifiDirectTransfer.Core;

public static class TextMessageCodec
{
    public const int MaxPayloadBytes = 64 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            var bytes = StrictUtf8.GetBytes(text);

            if (bytes.Length > MaxPayloadBytes)
            {
                throw new MessageProtocolException(
                    $"文本消息超过 {MaxPayloadBytes} 字节上限。");
            }

            return bytes;
        }
        catch (EncoderFallbackException exception)
        {
            throw new MessageProtocolException(
                "文本消息包含无法编码的 Unicode 字符。",
                exception);
        }
    }

    public static string Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadBytes)
        {
            throw new MessageProtocolException(
                $"文本消息超过 {MaxPayloadBytes} 字节上限。");
        }

        try
        {
            return StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException exception)
        {
            throw new MessageProtocolException(
                "文本消息不是有效的 UTF-8。",
                exception);
        }
    }
}

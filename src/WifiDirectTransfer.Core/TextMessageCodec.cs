using System.Text;

namespace WifiDirectTransfer.Core;

public static class TextMessageCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            return StrictUtf8.GetBytes(text);
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

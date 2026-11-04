using System.Text;
using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class TextMessageCodecTests
{
    [Fact]
    public void EncodeDecode_RoundTripsUtf8()
    {
        const string text = "文本消息 / hello 🚀";

        var payload = TextMessageCodec.Encode(text);

        Assert.Equal(Encoding.UTF8.GetByteCount(text), payload.Length);
        Assert.Equal(text, TextMessageCodec.Decode(payload));
    }

    [Fact]
    public void Decode_RejectsInvalidUtf8()
    {
        Assert.Throws<MessageProtocolException>(
            () => TextMessageCodec.Decode(new byte[] { 0xFF, 0xFE }));
    }

    [Fact]
    public void Encode_RejectsOverLimitText()
    {
        var text = new string('a', TextMessageCodec.MaxPayloadBytes + 1);

        var exception = Assert.Throws<MessageProtocolException>(
            () => TextMessageCodec.Encode(text));

        Assert.Contains("65536", exception.Message);
    }

    [Fact]
    public void Decode_RejectsOverLimitPayload()
    {
        var payload = new byte[TextMessageCodec.MaxPayloadBytes + 1];

        Assert.Throws<MessageProtocolException>(
            () => TextMessageCodec.Decode(payload));
    }

    [Fact]
    public void Encode_AllowsEmptyText()
    {
        var payload = TextMessageCodec.Encode(string.Empty);

        Assert.Empty(payload);
        Assert.Equal(string.Empty, TextMessageCodec.Decode(payload));
    }
}

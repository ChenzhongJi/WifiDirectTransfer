using System.Text;
using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class ClipboardMessageCodecTests
{
    [Fact]
    public void Text_RoundTripsUtf8()
    {
        const string text = "剪贴板文本 / clipboard";
        var payload = ClipboardMessageCodec.EncodeText(text);

        Assert.Equal((byte)ClipboardContentType.Text, payload[0]);
        Assert.Equal(
            Encoding.UTF8.GetBytes(text),
            payload.AsSpan(1).ToArray());

        var content = ClipboardMessageCodec.Deserialize(payload);

        Assert.Equal(ClipboardContentType.Text, content.Type);
        Assert.Equal(text, ClipboardMessageCodec.DecodeText(content.Data));
    }

    [Fact]
    public void Image_RoundTripsPngBytes()
    {
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x01, 0x02 };
        var payload = ClipboardMessageCodec.EncodeImage(pngBytes);

        Assert.Equal((byte)ClipboardContentType.Image, payload[0]);

        var content = ClipboardMessageCodec.Deserialize(payload);

        Assert.Equal(ClipboardContentType.Image, content.Type);
        Assert.Equal(pngBytes, content.Data);
    }

    [Fact]
    public void Deserialize_RejectsInvalidPayloads()
    {
        Assert.Throws<MessageProtocolException>(
            () => ClipboardMessageCodec.Deserialize(ReadOnlySpan<byte>.Empty));

        Assert.Throws<MessageProtocolException>(
            () => ClipboardMessageCodec.Deserialize(new byte[] { 0x42 }));

        Assert.Throws<MessageProtocolException>(
            () => ClipboardMessageCodec.EncodeImage(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void DecodeText_RejectsInvalidUtf8()
    {
        Assert.Throws<MessageProtocolException>(
            () => ClipboardMessageCodec.DecodeText(new byte[] { 0xFF }));
    }

    [Fact]
    public void EncodeImage_Over20Mb_Throws()
    {
        var pngBytes = new byte[ClipboardMessageCodec.MaxImageBytes + 1];

        Assert.Throws<MessageProtocolException>(
            () => ClipboardMessageCodec.EncodeImage(pngBytes));
    }
}

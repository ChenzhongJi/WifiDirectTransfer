using System.Buffers.Binary;
using System.Text;
using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class ControlMessageCodecTests
{
    [Fact]
    public void Ack_RoundTripsBigEndianOffset()
    {
        var payload = ControlMessageCodec.SerializeAck(0x0102030405060708);

        Assert.Equal((byte)ControlMessageSubtype.Ack, payload[0]);
        Assert.Equal(
            0x0102030405060708L,
            BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(1, 8)));

        var message = ControlMessageCodec.Deserialize(payload);

        Assert.Equal(ControlMessageSubtype.Ack, message.Subtype);
        Assert.Equal(0x0102030405060708L, message.Offset);
    }

    [Fact]
    public void ResumeRequest_RoundTripsOffset()
    {
        var payload = ControlMessageCodec.SerializeResumeRequest(123456789);
        var message = ControlMessageCodec.Deserialize(payload);

        Assert.Equal(ControlMessageSubtype.ResumeRequest, message.Subtype);
        Assert.Equal(123456789, message.Offset);
    }

    [Fact]
    public void TransferError_RoundTripsCodeAndUtf8Message()
    {
        var payload = ControlMessageCodec.SerializeTransferError(
            WifiDirectErrorCode.ConnectionTimeout,
            "连接超时，请重试。");

        Assert.Equal((byte)ControlMessageSubtype.TransferError, payload[0]);
        Assert.Equal(
            (int)WifiDirectErrorCode.ConnectionTimeout,
            BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(1, 4)));

        var message = ControlMessageCodec.Deserialize(payload);

        Assert.Equal(ControlMessageSubtype.TransferError, message.Subtype);
        Assert.Equal(WifiDirectErrorCode.ConnectionTimeout, message.ErrorCode);
        Assert.Equal("连接超时，请重试。", message.Message);
    }

    [Fact]
    public void QueueInfo_RoundTripsTotalBytes()
    {
        var payload = ControlMessageCodec.SerializeQueueInfo(12_345_678);
        var message = ControlMessageCodec.Deserialize(payload);

        Assert.Equal(ControlMessageSubtype.QueueInfo, message.Subtype);
        Assert.Equal(12_345_678, message.Offset);
    }

    [Fact]
    public void FileSkipped_RoundTrips()
    {
        var payload = ControlMessageCodec.SerializeFileSkipped();
        var message = ControlMessageCodec.Deserialize(payload);

        Assert.Single(payload);
        Assert.Equal(ControlMessageSubtype.FileSkipped, message.Subtype);
        Assert.Equal(WifiDirectErrorCode.FileSkipped, message.ErrorCode);
    }

    [Fact]
    public void DirectoryManifest_RoundTripsEntries()
    {
        var payload = ControlMessageCodec.SerializeDirectoryManifest(
            new[] { "empty", "photos/2026" });

        var message = ControlMessageCodec.Deserialize(payload);

        Assert.Equal(ControlMessageSubtype.DirectoryManifest, message.Subtype);
        Assert.Equal(new[] { "empty", "photos/2026" }, message.Directories);
    }

    [Fact]
    public void TextRejected_RoundTrips()
    {
        var payload = ControlMessageCodec.SerializeTextRejected();
        var message = ControlMessageCodec.Deserialize(payload);

        Assert.Single(payload);
        Assert.Equal(ControlMessageSubtype.TextRejected, message.Subtype);
    }

    [Fact]
    public void TextRejected_RejectsInvalidLength()
    {
        Assert.Throws<MessageProtocolException>(
            () => ControlMessageCodec.Deserialize(
                new[]
                {
                    (byte)ControlMessageSubtype.TextRejected,
                    (byte)1
                }));
    }

    [Fact]
    public void FileVerified_RoundTripsDigest()
    {
        var digest = new byte[MessageProtocol.FileEndDigestLength];

        for (var index = 0; index < digest.Length; index++)
        {
            digest[index] = (byte)index;
        }

        var payload = ControlMessageCodec.SerializeFileVerified(digest);

        Assert.Equal((byte)ControlMessageSubtype.FileVerified, payload[0]);
        Assert.Equal(ControlMessageCodec.FileVerifiedPayloadLength, payload.Length);

        var message = ControlMessageCodec.Deserialize(payload);

        Assert.Equal(ControlMessageSubtype.FileVerified, message.Subtype);
        Assert.Equal(digest, message.Digest);
    }

    [Fact]
    public void FileVerified_RejectsInvalidDigestLength()
    {
        Assert.Throws<MessageProtocolException>(
            () => ControlMessageCodec.SerializeFileVerified(
                new byte[MessageProtocol.FileEndDigestLength - 1]));

        Assert.Throws<MessageProtocolException>(
            () => ControlMessageCodec.Deserialize(
                new[]
                {
                    (byte)ControlMessageSubtype.FileVerified,
                    (byte)1,
                    (byte)2
                }));
    }

    [Fact]
    public void Hello_RoundTripsVersionLimitAndBuild()
    {
        var payload = ControlMessageCodec.SerializeHello(
            MessageProtocol.ProtocolVersion,
            33_554_433,
            "1.0.0+abc123");

        var message = ControlMessageCodec.Deserialize(payload);

        Assert.Equal(ControlMessageSubtype.Hello, message.Subtype);
        Assert.Equal(MessageProtocol.ProtocolVersion, message.ProtocolVersion);
        Assert.Equal(33_554_433, message.MaxPayloadLength);
        Assert.Equal("1.0.0+abc123", message.Message);
    }

    [Fact]
    public void Hello_RejectsInvalidPayloads()
    {
        Assert.Throws<MessageProtocolException>(
            () => ControlMessageCodec.SerializeHello(0, 1024, "build"));
        Assert.Throws<MessageProtocolException>(
            () => ControlMessageCodec.SerializeHello(1, 0, "build"));
        Assert.Throws<MessageProtocolException>(
            () => ControlMessageCodec.Deserialize(
                new[]
                {
                    (byte)ControlMessageSubtype.Hello,
                    (byte)0,
                    (byte)0,
                    (byte)0,
                    (byte)1
                }));
    }

    [Fact]
    public void TransferError_AllowsEmptyMessage()
    {
        var payload = ControlMessageCodec.SerializeTransferError(
            WifiDirectErrorCode.Unknown,
            string.Empty);

        var message = ControlMessageCodec.Deserialize(payload);

        Assert.Equal(string.Empty, message.Message);
    }

    [Fact]
    public void Deserialize_RejectsInvalidControlPayloads()
    {
        Assert.Throws<MessageProtocolException>(
            () => ControlMessageCodec.Deserialize(ReadOnlySpan<byte>.Empty));

        Assert.Throws<MessageProtocolException>(
            () => ControlMessageCodec.Deserialize(new byte[] { 0x42 }));

        Assert.Throws<MessageProtocolException>(
            () => ControlMessageCodec.Deserialize(
                new byte[] { (byte)ControlMessageSubtype.Ack, 1, 2, 3 }));

        Assert.Throws<MessageProtocolException>(
            () => ControlMessageCodec.Deserialize(
                new byte[] { (byte)ControlMessageSubtype.TransferError, 0, 0 }));

        Assert.Throws<MessageProtocolException>(
            () => ControlMessageCodec.SerializeAck(-1));
    }

    [Fact]
    public void Deserialize_RejectsInvalidUtf8TransferError()
    {
        var payload = new byte[1 + 4 + 1];
        payload[0] = (byte)ControlMessageSubtype.TransferError;
        payload[5] = 0xFF;

        Assert.Throws<MessageProtocolException>(
            () => ControlMessageCodec.Deserialize(payload));
    }

    [Fact]
    public void WindowModeStart_RoundTripsParameters()
    {
        var payload = ControlMessageCodec.SerializeWindowModeStart(
            16,
            4,
            0x0102030405060708);
        var message = ControlMessageCodec.Deserialize(payload);

        Assert.Equal(
            (byte)ControlMessageSubtype.WindowModeStart,
            payload[0]);
        Assert.Equal(ControlMessageSubtype.WindowModeStart, message.Subtype);
        Assert.Equal(16, message.WindowSize);
        Assert.Equal(4, message.AckInterval);
        Assert.Equal(0x0102030405060708, message.TransferId);
    }

    [Fact]
    public void WindowAck_RoundTripsTransferAndOffset()
    {
        var payload = ControlMessageCodec.SerializeWindowAck(
            0x0102030405060708,
            123456789);
        var message = ControlMessageCodec.Deserialize(payload);

        Assert.Equal((byte)ControlMessageSubtype.WindowAck, payload[0]);
        Assert.Equal(ControlMessageSubtype.WindowAck, message.Subtype);
        Assert.Equal(0x0102030405060708, message.TransferId);
        Assert.Equal(123456789, message.Offset);
    }

    [Fact]
    public void WindowModeStart_RejectsInvalidParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ControlMessageCodec.SerializeWindowModeStart(0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ControlMessageCodec.SerializeWindowModeStart(8, 9, 1));

        var payload = ControlMessageCodec.SerializeWindowModeStart(
            16,
            4,
            1);
        payload[5] = 0xFF;

        Assert.Throws<MessageProtocolException>(
            () => ControlMessageCodec.Deserialize(payload));
    }

    [Fact]
    public void TextMessage_RoundTripsUtf8()
    {
        const string text = "文本消息 / hello";

        var payload = TextMessageCodec.Encode(text);
        var decoded = TextMessageCodec.Decode(payload);

        Assert.Equal(text, decoded);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), payload.Length);
    }

    [Fact]
    public void TextMessage_RejectsInvalidUtf8()
    {
        Assert.Throws<MessageProtocolException>(
            () => TextMessageCodec.Decode(new byte[] { 0xFF }));
    }
}

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

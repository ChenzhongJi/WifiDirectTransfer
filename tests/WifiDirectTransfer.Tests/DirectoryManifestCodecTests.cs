using System.Buffers.Binary;
using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class DirectoryManifestCodecTests
{
    [Fact]
    public void Serialize_Deserialize_RoundTripsDirectories()
    {
        var directories = new[]
        {
            "empty",
            "photos/2026",
            "项目/空目录"
        };

        var payload = DirectoryManifestCodec.Serialize(directories);
        var roundTripped = DirectoryManifestCodec.Deserialize(payload);

        Assert.Equal((byte)ControlMessageSubtype.DirectoryManifest, payload[0]);
        Assert.Equal(DirectoryManifestCodec.Version, payload[1]);
        Assert.Equal(directories, roundTripped);
    }

    [Fact]
    public void Serialize_RejectsEmptyList()
    {
        Assert.Throws<MessageProtocolException>(
            () => DirectoryManifestCodec.Serialize(Array.Empty<string>()));
    }

    [Fact]
    public void Serialize_RejectsEmptyEntry()
    {
        Assert.Throws<MessageProtocolException>(
            () => DirectoryManifestCodec.Serialize(new[] { string.Empty }));
    }

    [Fact]
    public void Serialize_RejectsOverlongEntry()
    {
        var directory = new string(
            'a',
            FileTransferProtocol.MaxRelativeDirectoryLength + 1);

        Assert.Throws<MessageProtocolException>(
            () => DirectoryManifestCodec.Serialize(new[] { directory }));
    }

    [Fact]
    public void Serialize_RejectsPayloadAboveLimit()
    {
        var directory = new string('a', 4096);
        var directories = Enumerable
            .Range(0, 300)
            .Select(_ => directory)
            .ToArray();

        Assert.Throws<MessageProtocolException>(
            () => DirectoryManifestCodec.Serialize(directories));
    }

    [Fact]
    public void Deserialize_RejectsPayloadAboveLimit()
    {
        var payload = new byte[DirectoryManifestCodec.MaxPayloadLength + 1];
        payload[0] = (byte)ControlMessageSubtype.DirectoryManifest;
        payload[1] = DirectoryManifestCodec.Version;

        Assert.Throws<MessageProtocolException>(
            () => DirectoryManifestCodec.Deserialize(payload));
    }

    [Fact]
    public void Deserialize_RejectsWrongSubtype()
    {
        var payload = DirectoryManifestCodec.Serialize(new[] { "empty" });
        payload[0] = (byte)ControlMessageSubtype.Ack;

        Assert.Throws<MessageProtocolException>(
            () => DirectoryManifestCodec.Deserialize(payload));
    }

    [Fact]
    public void Deserialize_RejectsWrongVersion()
    {
        var payload = DirectoryManifestCodec.Serialize(new[] { "empty" });
        payload[1] = 99;

        Assert.Throws<MessageProtocolException>(
            () => DirectoryManifestCodec.Deserialize(payload));
    }

    [Fact]
    public void Deserialize_RejectsCountMismatch()
    {
        var payload = DirectoryManifestCodec.Serialize(new[] { "empty" });
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(2, 4), 2);

        Assert.Throws<MessageProtocolException>(
            () => DirectoryManifestCodec.Deserialize(payload));
    }

    [Fact]
    public void Deserialize_RejectsTrailingData()
    {
        var payload = DirectoryManifestCodec.Serialize(new[] { "empty" });
        Array.Resize(ref payload, payload.Length + 1);

        Assert.Throws<MessageProtocolException>(
            () => DirectoryManifestCodec.Deserialize(payload));
    }

    [Fact]
    public void Deserialize_RejectsTruncatedEntry()
    {
        var payload = DirectoryManifestCodec.Serialize(new[] { "empty" });
        Array.Resize(ref payload, payload.Length - 1);

        Assert.Throws<MessageProtocolException>(
            () => DirectoryManifestCodec.Deserialize(payload));
    }
}

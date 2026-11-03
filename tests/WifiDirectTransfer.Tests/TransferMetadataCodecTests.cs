using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class TransferMetadataCodecTests
{
    [Fact]
    public void Serialize_Deserialize_RoundTrips()
    {
        var metadata = new TransferMetadata(
            "photo.jpg",
            1_234_567_890,
            65_536);

        var bytes = TransferMetadataCodec.Serialize(metadata);

        Assert.True(
            TransferMetadataCodec.TryDeserialize(bytes, out var parsed));
        Assert.Equal(metadata, parsed);
    }

    [Fact]
    public void TryDeserialize_RejectsInvalidData()
    {
        Assert.False(
            TransferMetadataCodec.TryDeserialize(
                ReadOnlySpan<byte>.Empty,
                out _));

        var valid = TransferMetadataCodec.Serialize(
            new TransferMetadata("a.bin", 10, 5));

        var badMagic = valid.ToArray();
        badMagic[0] ^= 0xFF;
        Assert.False(
            TransferMetadataCodec.TryDeserialize(badMagic, out _));

        var truncated = valid[..^1];
        Assert.False(
            TransferMetadataCodec.TryDeserialize(truncated, out _));

        var badSize = valid.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
            badSize.AsSpan(TransferMetadataCodec.HeaderLength + 5, 8),
            -1);
        Assert.False(
            TransferMetadataCodec.TryDeserialize(badSize, out _));
    }

    [Fact]
    public void Serialize_RejectsInvalidSizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TransferMetadataCodec.Serialize(
                new TransferMetadata("a.bin", -1, 0)));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => TransferMetadataCodec.Serialize(
                new TransferMetadata("a.bin", 10, 11)));
    }
}

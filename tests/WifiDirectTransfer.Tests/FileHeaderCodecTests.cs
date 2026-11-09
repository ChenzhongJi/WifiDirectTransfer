using System.Buffers.Binary;
using System.Text;
using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class FileHeaderCodecTests
{
    [Fact]
    public void Serialize_Deserialize_RoundTripsHeader()
    {
        var codec = new FileHeaderCodec();
        var header = FileHeader.CreateFileStart(
            fileSize: 1024 * 1024,
            fileNameByteLength: 12,
            flags: FileFlags.LastFile);

        var bytes = codec.Serialize(header);
        var roundTripped = codec.Deserialize(bytes);

        Assert.Equal(FileTransferProtocol.FixedHeaderLength, bytes.Length);
        Assert.Equal(header, roundTripped);
        Assert.Equal(FileTransferProtocol.MagicValue, roundTripped.Magic);
        Assert.Equal(FileTransferProtocol.Version, roundTripped.Version);
        Assert.Equal(MessageType.FileStart, roundTripped.MessageType);
        Assert.Equal(ChecksumAlgorithm.Sha256, roundTripped.ChecksumAlgorithm);
        Assert.Equal(FileFlags.LastFile, roundTripped.Flags);
    }

    [Fact]
    public void Serialize_UsesLittleEndianLayout()
    {
        var codec = new FileHeaderCodec(long.MaxValue);
        var header = FileHeader.CreateFileStart(
            fileSize: 0x0102030405060708,
            fileNameByteLength: 5,
            flags: FileFlags.LastFile);

        var bytes = codec.Serialize(header);

        var expected = new byte[]
        {
            0x57, 0x46, 0x44, 0x31,                         // Magic "WFD1"
            0x02,                                           // Version
            0x01,                                           // MessageType.FileStart
            0x1D, 0x00,                                     // HeaderLength 29
            0x05, 0x00,                                     // FileNameLength 5
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01, // FileSize little-endian
            0x01,                                           // ChecksumAlgorithm.Sha256
            0x01,                                           // FileFlags.LastFile
            0x00, 0x00, 0x00, 0x00                          // Reserved
        };

        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void Serialize_Deserialize_RoundTripsZeroByteFile()
    {
        var codec = new FileHeaderCodec();
        var header = FileHeader.CreateFileStart(0, fileNameByteLength: 8);

        var roundTripped = codec.Deserialize(codec.Serialize(header));

        Assert.Equal(0, roundTripped.FileSize);
        Assert.Equal(header, roundTripped);
    }

    [Fact]
    public void Serialize_Deserialize_AllowsDefaultMaximumFile()
    {
        var codec = new FileHeaderCodec();
        var size = FileTransferProtocol.DefaultMaxFileSize;
        var header = FileHeader.CreateFileStart(size, fileNameByteLength: 8);

        var roundTripped = codec.Deserialize(codec.Serialize(header));

        Assert.Equal(size, roundTripped.FileSize);
    }

    [Fact]
    public void Serialize_Deserialize_AllowsFileAboveTwoGiB()
    {
        var codec = new FileHeaderCodec();
        var size = 3L * 1024 * 1024 * 1024;
        var header = FileHeader.CreateFileStart(size, fileNameByteLength: 8);

        var roundTripped = codec.Deserialize(codec.Serialize(header));

        Assert.Equal(size, roundTripped.FileSize);
    }

    [Fact]
    public void Deserialize_RejectsFileAboveDefaultMaximum()
    {
        var permissiveCodec = new FileHeaderCodec(long.MaxValue);
        var header = FileHeader.CreateFileStart(
            FileTransferProtocol.DefaultMaxFileSize + 1,
            fileNameByteLength: 8);
        var bytes = permissiveCodec.Serialize(header);

        Assert.Throws<InvalidFileHeaderException>(
            () => new FileHeaderCodec().Deserialize(bytes));
    }

    [Fact]
    public void EncodeFileName_RejectsOverlongUtf8Name()
    {
        Assert.Throws<InvalidFileHeaderException>(
            () => FileHeaderCodec.EncodeFileName(new string('a', 256)));

        Assert.Throws<InvalidFileHeaderException>(
            () => FileHeaderCodec.EncodeFileName(new string('中', 100)));
    }

    [Fact]
    public void EncodeFileName_RejectsEmptyName()
    {
        Assert.Throws<InvalidFileHeaderException>(
            () => FileHeaderCodec.EncodeFileName(string.Empty));
    }

    [Fact]
    public void FileNameCodec_DoesNotSanitizeNames()
    {
        const string rawName = "../CON";

        var encoded = FileHeaderCodec.EncodeFileName(rawName);
        var decoded = FileHeaderCodec.DecodeFileName(encoded);

        Assert.Equal(rawName, decoded);
    }

    [Fact]
    public void FileNameCodec_RoundTripsUtf8Name()
    {
        const string name = "测试文件.bin";

        var encoded = FileHeaderCodec.EncodeFileName(name);

        Assert.Equal(Encoding.UTF8.GetByteCount(name), encoded.Length);
        Assert.Equal(name, FileHeaderCodec.DecodeFileName(encoded));
    }

    [Fact]
    public void EncodeFileName_AcceptsMaximumLengthName()
    {
        var name = new string('a', FileTransferProtocol.MaxFileNameLength);

        Assert.Equal(
            FileTransferProtocol.MaxFileNameLength,
            FileHeaderCodec.EncodeFileName(name).Length);
    }

    [Fact]
    public void FileStartPayload_RoundTripsHeaderAndRawFileName()
    {
        var codec = new FileHeaderCodec();
        var fileNameBytes = FileHeaderCodec.EncodeFileName("../CON.txt");
        var header = FileHeader.CreateFileStart(
            fileSize: 12345,
            fileNameByteLength: fileNameBytes.Length);

        var payload = codec.SerializeFileStartPayload(header, fileNameBytes);
        var roundTripped = codec.DeserializeFileStartPayload(payload);

        Assert.Equal(
            FileTransferProtocol.FixedHeaderLength + fileNameBytes.Length,
            payload.Length);
        Assert.Equal(header, roundTripped.Header);
        Assert.Equal("../CON.txt", roundTripped.FileName);
        Assert.Equal(string.Empty, roundTripped.RelativeDirectory);
    }

    [Fact]
    public void FileStartPayload_RoundTripsRelativeDirectory()
    {
        var codec = new FileHeaderCodec();
        var fileNameBytes = FileHeaderCodec.EncodeFileName("report.pdf");
        var relativeBytes = FileHeaderCodec.EncodeRelativeDirectory("docs/2026");
        var header = FileHeader.CreateFileStart(
            fileSize: 12345,
            fileNameByteLength: fileNameBytes.Length,
            relativeDirectoryByteLength: relativeBytes.Length);

        var payload = codec.SerializeFileStartPayload(
            header,
            fileNameBytes,
            relativeBytes);
        var roundTripped = codec.DeserializeFileStartPayload(payload);

        Assert.Equal(
            FileTransferProtocol.FixedHeaderLength +
            fileNameBytes.Length +
            relativeBytes.Length,
            payload.Length);
        Assert.Equal(header, roundTripped.Header);
        Assert.Equal("report.pdf", roundTripped.FileName);
        Assert.Equal("docs/2026", roundTripped.RelativeDirectory);
    }

    [Fact]
    public void FileStartPayload_RejectsHeaderLengthMismatchForRelativeDirectory()
    {
        var codec = new FileHeaderCodec();
        var fileNameBytes = FileHeaderCodec.EncodeFileName("report.pdf");
        var relativeBytes = FileHeaderCodec.EncodeRelativeDirectory("docs");
        var header = FileHeader.CreateFileStart(
            fileSize: 12345,
            fileNameByteLength: fileNameBytes.Length);

        Assert.Throws<InvalidFileHeaderException>(
            () => codec.SerializeFileStartPayload(
                header,
                fileNameBytes,
                relativeBytes));
    }

    [Fact]
    public void FileStartPayload_RejectsTrailingOrMissingBytes()
    {
        var codec = new FileHeaderCodec();
        var fileNameBytes = FileHeaderCodec.EncodeFileName("file.bin");
        var header = FileHeader.CreateFileStart(10, fileNameBytes.Length);
        var payload = codec.SerializeFileStartPayload(header, fileNameBytes);

        Assert.Throws<InvalidFileHeaderException>(
            () => codec.DeserializeFileStartPayload(payload[..^1]));

        var extended = new byte[payload.Length + 1];
        payload.CopyTo(extended, 0);

        Assert.Throws<InvalidFileHeaderException>(
            () => codec.DeserializeFileStartPayload(extended));
    }

    [Fact]
    public void FileStartPayload_RejectsFileNameLengthMismatch()
    {
        var codec = new FileHeaderCodec();
        var header = FileHeader.CreateFileStart(10, fileNameByteLength: 4);

        Assert.Throws<InvalidFileHeaderException>(
            () => codec.SerializeFileStartPayload(header, FileHeaderCodec.EncodeFileName("toolong.bin")));
    }

    [Fact]
    public void DecodeFileName_RejectsInvalidUtf8()
    {
        Assert.Throws<InvalidFileHeaderException>(
            () => FileHeaderCodec.DecodeFileName(new byte[] { 0xFF }));
    }

    [Fact]
    public void Deserialize_RejectsShortBuffer()
    {
        var codec = new FileHeaderCodec();

        Assert.Throws<InvalidFileHeaderException>(
            () => codec.Deserialize(new byte[FileTransferProtocol.FixedHeaderLength - 1]));
    }

    [Fact]
    public void Deserialize_RejectsInvalidFields()
    {
        var codec = new FileHeaderCodec(maxFileSize: 1024);
        var header = FileHeader.CreateFileStart(100, fileNameByteLength: 4);
        var validBytes = codec.Serialize(header);

        AssertRejected(codec, validBytes, bytes => bytes[0] ^= 0xFF);
        AssertRejected(codec, validBytes, bytes => bytes[4] = 1);
        AssertRejected(codec, validBytes, bytes => bytes[5] = (byte)MessageType.FileChunk);
        AssertRejected(
            codec,
            validBytes,
            bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 25));
        AssertRejected(
            codec,
            validBytes,
            bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 0));
        AssertRejected(
            codec,
            validBytes,
            bytes =>
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 256);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 280);
            });
        AssertRejected(
            codec,
            validBytes,
            bytes => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(10, 8), 1025));
        AssertRejected(codec, validBytes, bytes => bytes[18] = (byte)ChecksumAlgorithm.None);
        AssertRejected(codec, validBytes, bytes => bytes[19] = 0x02);
        AssertRejected(
            codec,
            validBytes,
            bytes => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), 1));
    }

    [Fact]
    public void Deserialize_RejectsFileSizeAboveConfiguredMaximum()
    {
        var codec = new FileHeaderCodec(maxFileSize: 10);
        var bytes = new FileHeaderCodec()
            .Serialize(FileHeader.CreateFileStart(11, fileNameByteLength: 4));

        Assert.Throws<InvalidFileHeaderException>(() => codec.Deserialize(bytes));
    }

    private static void AssertRejected(
        FileHeaderCodec codec,
        byte[] validBytes,
        Action<byte[]> mutate)
    {
        var bytes = validBytes.ToArray();
        mutate(bytes);

        Assert.Throws<InvalidFileHeaderException>(() => codec.Deserialize(bytes));
    }
}

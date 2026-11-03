using System.Text;
using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class SafeFileNameTests
{
    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("COM1.tar.gz", "_COM1.tar.gz")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("CONIN$", "_CONIN$")]
    public void Sanitize_PrefixesWindowsReservedNames(string input, string expected)
    {
        Assert.Equal(expected, SafeFileName.Sanitize(input));
    }

    [Fact]
    public void Sanitize_AllowsNonReservedCom10()
    {
        Assert.Equal("COM10", SafeFileName.Sanitize("COM10"));
    }

    [Theory]
    [InlineData(@"..\..\evil.txt", "evil.txt")]
    [InlineData("folder/sub/file.txt", "file.txt")]
    [InlineData(@"C:\temp\..\file.txt", "file.txt")]
    [InlineData(@"..\CON", "_CON")]
    public void Sanitize_RemovesDirectoryTraversal(string input, string expected)
    {
        Assert.Equal(expected, SafeFileName.Sanitize(input));
    }

    [Fact]
    public void Sanitize_RemovesTraversalOnlyName()
    {
        Assert.Equal("file", SafeFileName.Sanitize(".."));
    }

    [Theory]
    [InlineData("report. . ", "report")]
    [InlineData("name...", "name")]
    [InlineData("name. ", "name")]
    public void Sanitize_TrimsTrailingDotsAndSpaces(string input, string expected)
    {
        Assert.Equal(expected, SafeFileName.Sanitize(input));
    }

    [Fact]
    public void Sanitize_ReplacesInvalidCharacters()
    {
        var result = SafeFileName.Sanitize("bad:name?.txt");

        Assert.Equal("bad_name_.txt", result);
        Assert.DoesNotContain(result, character => Path.GetInvalidFileNameChars().Contains(character));
    }

    [Fact]
    public void Sanitize_ReplacesDoubleDotsInsideName()
    {
        Assert.Equal("a_b.txt", SafeFileName.Sanitize("a..b.txt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" . ")]
    public void Sanitize_FallsBackToFile(string input)
    {
        Assert.Equal("file", SafeFileName.Sanitize(input));
    }

    [Fact]
    public void Sanitize_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => SafeFileName.Sanitize(null!));
    }

    [Fact]
    public void Sanitize_TruncatesAsciiToProtocolLimit()
    {
        var result = SafeFileName.Sanitize(new string('a', 300));

        Assert.Equal(FileTransferProtocol.MaxFileNameLength, result.Length);
        Assert.Equal(
            FileTransferProtocol.MaxFileNameLength,
            Encoding.UTF8.GetByteCount(result));
    }

    [Fact]
    public void Sanitize_TruncatesMultibyteNameWithoutSplittingCharacters()
    {
        var result = SafeFileName.Sanitize(new string('中', 100));

        Assert.Equal(85, result.Length);
        Assert.Equal(255, Encoding.UTF8.GetByteCount(result));
    }
}

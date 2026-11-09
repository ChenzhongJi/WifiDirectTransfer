using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class SafeRelativePathTests
{
    [Fact]
    public void Normalize_EmptyOrWhitespace_ReturnsRoot()
    {
        Assert.Equal(string.Empty, SafeRelativePath.Normalize(null));
        Assert.Equal(string.Empty, SafeRelativePath.Normalize(string.Empty));
        Assert.Equal(string.Empty, SafeRelativePath.Normalize("   "));
    }

    [Fact]
    public void Normalize_JoinsSegmentsAndSanitizesInvalidNames()
    {
        var normalized = SafeRelativePath.Normalize("docs/CON/a?b");

        Assert.Equal(
            Path.Combine("docs", "_CON", "a_b"),
            normalized);
        Assert.Equal("docs/_CON/a_b", SafeRelativePath.ToWire("docs\\CON\\a?b"));
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("docs/../../secret")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("\\\\server\\share")]
    [InlineData("docs/./child")]
    [InlineData("docs:stream")]
    public void Normalize_RejectsUnsafePaths(string path)
    {
        Assert.Throws<InvalidFileHeaderException>(
            () => SafeRelativePath.Normalize(path));
    }

    [Fact]
    public void ResolveFile_CreatesPathUnderRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "wdt-safe-path");

        var resolved = SafeRelativePath.ResolveFile(
            root,
            "docs/2026",
            "report.pdf");

        Assert.StartsWith(
            Path.GetFullPath(root) + Path.DirectorySeparatorChar,
            resolved,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(
            Path.Combine("docs", "2026", "report.pdf"),
            resolved,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveFile_RejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "wdt-safe-path");

        Assert.Throws<InvalidFileHeaderException>(
            () => SafeRelativePath.ResolveFile(
                root,
                "..",
                "report.pdf"));

        Assert.Throws<InvalidFileHeaderException>(
            () => SafeRelativePath.ResolveDirectory(
                root,
                "/absolute"));
    }

    [Fact]
    public void ResolveFile_SanitizesLeafFileName()
    {
        var root = Path.Combine(Path.GetTempPath(), "wdt-safe-path");

        var resolved = SafeRelativePath.ResolveFile(
            root,
            "docs",
            "../CON.txt");

        Assert.EndsWith("CON.txt", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            Path.GetFullPath(root) + Path.DirectorySeparatorChar,
            resolved,
            StringComparison.OrdinalIgnoreCase);
    }
}

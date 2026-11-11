using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.6.0", 1, 6, 0, 0)]
    [InlineData("1.6", 1, 6, 0, 0)]
    [InlineData("1.6.0.7", 1, 6, 0, 7)]
    [InlineData("0.0.1", 0, 0, 1, 0)]
    [InlineData("1.6.0+abc123", 1, 6, 0, 0)]
    [InlineData("1.6.0-preview.2", 1, 6, 0, 0)]
    [InlineData(" 2.10.3 ", 2, 10, 3, 0)]
    public void TryParse_AcceptsNumbersAndBuildSuffix(
        string text,
        int major,
        int minor,
        int build,
        int revision)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(build, version.Build);
        Assert.Equal(revision, version.Revision);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("v1.6.0")]
    [InlineData("1..0")]
    [InlineData("1.6.0.0.1")]
    [InlineData("1.-1")]
    [InlineData("1.6.x")]
    [InlineData("1,6,0")]
    [InlineData("0x10")]
    public void TryParse_RejectsInvalidText(string text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out _));
    }

    [Fact]
    public void CompareTo_UsesNumericOrderNotTextOrder()
    {
        var older = ReleaseVersion.Parse("1.9.0");
        var newer = ReleaseVersion.Parse("1.10.0");

        Assert.True(newer > older);
        Assert.True(older < newer);
        Assert.False(newer == older);
    }

    [Fact]
    public void CompareTo_TreatsMissingRevisionAsZero()
    {
        Assert.Equal(
            ReleaseVersion.Parse("1.6.0"),
            ReleaseVersion.Parse("1.6.0.0"));
    }

    [Fact]
    public void ToString_DropsZeroRevision()
    {
        Assert.Equal("1.6.0", ReleaseVersion.Parse("1.6.0").ToString());
        Assert.Equal("1.6.0.4", ReleaseVersion.Parse("1.6.0.4").ToString());
    }

    [Fact]
    public void Parse_IgnoresCommitMetadataFromAssemblyVersion()
    {
        var version = ReleaseVersion.Parse("1.6.0+3f2c1ab");

        Assert.Equal("1.6.0", version.ToString());
    }
}

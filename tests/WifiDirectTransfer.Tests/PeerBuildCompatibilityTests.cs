using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class PeerBuildCompatibilityTests
{
    [Theory]
    [InlineData("1.1.1+3f131b1", FileTransferProtocol.LegacyMaxFileSize)]
    [InlineData("1.1.0", FileTransferProtocol.LegacyMaxFileSize)]
    [InlineData("1.0.2+abc", FileTransferProtocol.LegacyMaxFileSize)]
    [InlineData("1.2.0+abc", FileTransferProtocol.DefaultMaxFileSize)]
    [InlineData("1.2.0-preview", FileTransferProtocol.DefaultMaxFileSize)]
    [InlineData("2.0.0", FileTransferProtocol.DefaultMaxFileSize)]
    [InlineData("unknown", FileTransferProtocol.LegacyMaxFileSize)]
    [InlineData(null, FileTransferProtocol.LegacyMaxFileSize)]
    public void GetMaxFileSize_UsesPeerVersion(string? build, long expected)
    {
        Assert.Equal(expected, PeerBuildCompatibility.GetMaxFileSize(build));
    }

    [Theory]
    [InlineData("1.2.0", false)]
    [InlineData("1.2.9+abc", false)]
    [InlineData("1.3.0", true)]
    [InlineData("1.3.0+abc", true)]
    [InlineData("1.4.0-preview", true)]
    [InlineData("2.0.0", true)]
    [InlineData("unknown", false)]
    [InlineData(null, false)]
    public void SupportsDirectoryManifest_UsesPeerVersion(
        string? build,
        bool expected)
    {
        Assert.Equal(
            expected,
            PeerBuildCompatibility.SupportsDirectoryManifest(build));
    }
}

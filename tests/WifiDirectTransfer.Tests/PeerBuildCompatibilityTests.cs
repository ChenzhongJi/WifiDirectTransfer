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
}

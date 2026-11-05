using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class PeerIdentityMatcherTests
{
    [Theory]
    [InlineData("JICZ2-PF4TK4DF", "JICZ2-PF4TK4DF", true)]
    [InlineData("  JICZ2-PF4TK4DF  ", "jicz2-pf4tk4df", true)]
    [InlineData("DESKTOP-A", "DESKTOP-B", false)]
    [InlineData("", "DESKTOP-A", false)]
    [InlineData("未命名设备", "未命名设备", false)]
    [InlineData("未命名设备", "DESKTOP-A", false)]
    public void IsSameDisplayName_RespectsIdentityRules(
        string left,
        string right,
        bool expected)
    {
        Assert.Equal(
            expected,
            PeerIdentityMatcher.IsSameDisplayName(left, right));
    }

    [Fact]
    public void FindUniqueByName_ReturnsSingleMatch()
    {
        var peers = new[]
        {
            new WifiDirectPeer("old-id", "JICZ2-PF4TK4DF"),
            new WifiDirectPeer("other-id", "DESKTOP-7F3K")
        };

        var match = PeerIdentityMatcher.FindUniqueByName(
            peers,
            "jicz2-pf4tk4df");

        Assert.NotNull(match);
        Assert.Equal("old-id", match!.Id);
    }

    [Fact]
    public void FindUniqueByName_RejectsAmbiguousNames()
    {
        var peers = new[]
        {
            new WifiDirectPeer("id-1", "DESKTOP-A"),
            new WifiDirectPeer("id-2", "DESKTOP-A")
        };

        Assert.Null(
            PeerIdentityMatcher.FindUniqueByName(peers, "DESKTOP-A"));
    }

    [Fact]
    public void FindUniqueByName_RejectsUnknownNames()
    {
        var peers = new[]
        {
            new WifiDirectPeer("id-1", "DESKTOP-A")
        };

        Assert.Null(
            PeerIdentityMatcher.FindUniqueByName(peers, "DESKTOP-B"));
    }
}

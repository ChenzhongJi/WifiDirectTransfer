using System.Reflection;
using WdtApp = WifiDirectTransfer.App.App;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class UpdateServiceTests
{
    private static readonly MethodInfo IsAllowedDownloadUriMethod =
        typeof(WdtApp).Assembly
            .GetType("WifiDirectTransfer.App.UpdateService")!
            .GetMethod(
                "IsAllowedDownloadUri",
                BindingFlags.NonPublic | BindingFlags.Static)!;

    [Theory]
    [InlineData("file:///C:/updates/setup.exe")]
    [InlineData("http://127.0.0.1/setup.exe")]
    [InlineData("http://localhost/setup.exe")]
    public void LocalAndLoopbackDownloadUris_AreDebugOnly(string uriText)
    {
        var allowed = (bool)IsAllowedDownloadUriMethod.Invoke(
            null,
            new object[] { new Uri(uriText) })!;

#if DEBUG
        Assert.True(allowed);
#else
        Assert.False(allowed);
#endif
    }

    [Theory]
    [InlineData("https://github.com/ChenzhongJi/WifiDirectTransfer/releases/download/v1.6.4/setup.exe")]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/setup.exe")]
    public void GithubHttpsDownloadUris_AreAllowed(string uriText)
    {
        var allowed = (bool)IsAllowedDownloadUriMethod.Invoke(
            null,
            new object[] { new Uri(uriText) })!;

        Assert.True(allowed);
    }

    [Fact]
    public void NonGithubHttpsDownloadUri_IsRejected()
    {
        var allowed = (bool)IsAllowedDownloadUriMethod.Invoke(
            null,
            new object[] { new Uri("https://example.com/setup.exe") })!;

        Assert.False(allowed);
    }
}

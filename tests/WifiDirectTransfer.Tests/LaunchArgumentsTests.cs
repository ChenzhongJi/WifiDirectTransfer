using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class LaunchArgumentsTests
{
    [Fact]
    public void ParsePaths_ReadsSendSwitchWithMultiplePaths()
    {
        var paths = LaunchArguments.ParsePaths(
            new[] { "--send", @"C:\a.txt", @"D:\folder" });

        Assert.Equal(new[] { @"C:\a.txt", @"D:\folder" }, paths);
    }

    [Fact]
    public void ParsePaths_ReadsSendSwitchWithEquals()
    {
        var paths = LaunchArguments.ParsePaths(
            new[] { @"--send=C:\a.txt" });

        Assert.Equal(new[] { @"C:\a.txt" }, paths);
    }

    [Fact]
    public void ParsePaths_AcceptsBarePathsForSendTo()
    {
        var paths = LaunchArguments.ParsePaths(
            new[] { @"C:\a.txt", @"C:\b.txt" });

        Assert.Equal(new[] { @"C:\a.txt", @"C:\b.txt" }, paths);
    }

    [Fact]
    public void ParsePaths_IgnoresUnknownSwitchesAndDuplicates()
    {
        var paths = LaunchArguments.ParsePaths(
            new[]
            {
                "--minimized",
                @"C:\a.txt",
                @"C:\A.TXT",
                "--send",
                @"C:\b.txt",
                "--other"
            });

        Assert.Equal(new[] { @"C:\a.txt", @"C:\b.txt" }, paths);
    }

    [Fact]
    public void ParsePaths_StripsWrappingQuotes()
    {
        var paths = LaunchArguments.ParsePaths(
            new[] { "\"C:\\Program Files\\a.txt\"" });

        Assert.Equal(
            new[] { @"C:\Program Files\a.txt" },
            paths);
    }

    [Fact]
    public void ParsePaths_EmptyInputReturnsEmpty()
    {
        Assert.Empty(LaunchArguments.ParsePaths(null));
        Assert.Empty(LaunchArguments.ParsePaths(Array.Empty<string>()));
        Assert.Empty(LaunchArguments.ParsePaths(new[] { "--send" }));
    }
}

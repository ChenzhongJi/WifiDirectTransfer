using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class ThrottledProgressTests
{
    [Fact]
    public void Report_FirstAndFinalAreAlwaysDelivered()
    {
        var reported = new List<FileTransferProgress>();
        var progress = new ThrottledProgress<FileTransferProgress>(
            reported.Add,
            TimeSpan.FromHours(1),
            value => value.TotalBytes > 0 &&
                     value.BytesTransferred >= value.TotalBytes);

        progress.Report(new FileTransferProgress(1, 10));
        progress.Report(new FileTransferProgress(2, 10));
        progress.Report(new FileTransferProgress(10, 10));

        Assert.Equal(
            new[] { 1L, 10L },
            reported.Select(value => value.BytesTransferred));
    }

    [Fact]
    public void Report_ZeroInterval_ForwardsEveryValue()
    {
        var reported = new List<int>();
        var progress = new ThrottledProgress<int>(
            reported.Add,
            TimeSpan.Zero);

        progress.Report(1);
        progress.Report(2);
        progress.Report(3);

        Assert.Equal(new[] { 1, 2, 3 }, reported);
    }

    [Fact]
    public void Report_FinalPredicate_OverridesThrottle()
    {
        var reported = new List<int>();
        var progress = new ThrottledProgress<int>(
            reported.Add,
            TimeSpan.FromHours(1),
            value => value == 99);

        progress.Report(1);
        progress.Report(2);
        progress.Report(99);

        Assert.Equal(new[] { 1, 99 }, reported);
    }
}

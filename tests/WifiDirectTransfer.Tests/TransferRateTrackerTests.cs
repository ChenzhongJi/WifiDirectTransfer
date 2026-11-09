using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class TransferRateTrackerTests
{
    [Fact]
    public void Update_FirstSampleReturnsInstantaneousRate()
    {
        var tracker = new TransferRateTracker(TimeSpan.Zero);

        tracker.Update(1024 * 1024, TimeSpan.FromSeconds(1));

        Assert.True(tracker.HasValue);
        Assert.Equal(1024d * 1024d, tracker.BytesPerSecond, 3);
    }

    [Fact]
    public void Update_IgnoresSamplesInsideMinimumInterval()
    {
        var tracker = new TransferRateTracker(TimeSpan.FromMilliseconds(500));

        tracker.Update(100, TimeSpan.FromMilliseconds(100));

        Assert.False(tracker.HasValue);
        Assert.Equal(0d, tracker.BytesPerSecond);
    }

    [Fact]
    public void Update_AccumulatesBytesAcrossSkippedSamples()
    {
        var tracker = new TransferRateTracker(TimeSpan.FromMilliseconds(500));

        tracker.Update(100, TimeSpan.FromMilliseconds(100));
        tracker.Update(200, TimeSpan.FromMilliseconds(600));

        Assert.True(tracker.HasValue);
        Assert.Equal(200d / 0.6d, tracker.BytesPerSecond, 3);
    }

    [Fact]
    public void GetRemaining_UsesSmoothedRateAndRemainingBytes()
    {
        var tracker = new TransferRateTracker(TimeSpan.Zero);

        tracker.Update(2 * 1024 * 1024, TimeSpan.FromSeconds(2));

        var remaining = tracker.GetRemaining(10 * 1024 * 1024);

        Assert.NotNull(remaining);
        Assert.Equal(8d, remaining.Value.TotalSeconds, 3);
    }

    [Fact]
    public void GetRemaining_ReturnsZeroWhenComplete()
    {
        var tracker = new TransferRateTracker(TimeSpan.Zero);

        tracker.Update(1000, TimeSpan.FromSeconds(1));

        var remaining = tracker.GetRemaining(1000);

        Assert.Equal(TimeSpan.Zero, remaining);
    }

    [Fact]
    public void Reset_ClearsRate()
    {
        var tracker = new TransferRateTracker(TimeSpan.Zero);

        tracker.Update(1000, TimeSpan.FromSeconds(1));
        tracker.Reset();

        Assert.False(tracker.HasValue);
        Assert.Equal(0d, tracker.BytesPerSecond);
        Assert.Null(tracker.GetRemaining(2000));
    }

    [Fact]
    public void Update_AfterBackwardsProgressResetsRate()
    {
        var tracker = new TransferRateTracker(TimeSpan.Zero);

        tracker.Update(1000, TimeSpan.FromSeconds(1));
        tracker.Update(500, TimeSpan.FromSeconds(2));

        Assert.False(tracker.HasValue);
        Assert.Equal(0d, tracker.BytesPerSecond);
    }

    [Theory]
    [InlineData(0d, "—")]
    [InlineData(512d, "512 B/s")]
    [InlineData(1536d, "1.5 KB/s")]
    [InlineData(2d * 1024 * 1024, "2.0 MB/s")]
    public void FormatSpeed_UsesReadableUnits(double value, string expected)
    {
        Assert.Equal(expected, TransferFormat.FormatSpeed(value));
    }

    [Theory]
    [InlineData(0, "不到 1 秒")]
    [InlineData(65, "01:05")]
    [InlineData(3725, "1:02:05")]
    public void FormatDuration_UsesCompactForm(int totalSeconds, string expected)
    {
        Assert.Equal(
            expected,
            TransferFormat.FormatDuration(TimeSpan.FromSeconds(totalSeconds)));
    }
}

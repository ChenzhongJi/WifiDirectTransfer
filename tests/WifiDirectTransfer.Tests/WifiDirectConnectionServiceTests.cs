using WifiDirectTransfer.Core;
using WifiDirectTransfer.Windows;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class WifiDirectConnectionServiceTests
{
    [Fact]
    public async Task Service_StartsIdle_AndStopsIdle()
    {
        await using var service = new WifiDirectConnectionService();

        Assert.Equal(WifiDirectState.Idle, service.State);

        await service.StopAsync();

        Assert.Equal(WifiDirectState.Idle, service.State);
    }

    [Fact]
    public async Task FindPeersAsync_BeforeStart_ThrowsInvalidState()
    {
        await using var service = new WifiDirectConnectionService();

        await Assert.ThrowsAsync<WifiDirectInvalidStateException>(
            () => service.FindPeersAsync(TimeSpan.FromSeconds(1)));
    }
}

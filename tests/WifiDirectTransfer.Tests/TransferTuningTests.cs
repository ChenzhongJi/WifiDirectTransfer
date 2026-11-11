using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class TransferTuningTests
{
    [Theory]
    [InlineData(-100, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(32, 32)]
    [InlineData(64, 64)]
    [InlineData(65, 64)]
    [InlineData(int.MaxValue, 64)]
    public void ClampWindowBlocks_KeepsValueInsideProtocolRange(
        int requested,
        int expected)
    {
        Assert.Equal(expected, TransferTuning.ClampWindowBlocks(requested));
    }

    [Theory]
    [InlineData(-1, 32, 1)]
    [InlineData(0, 32, 1)]
    [InlineData(1, 32, 1)]
    [InlineData(8, 32, 8)]
    [InlineData(32, 32, 32)]
    [InlineData(33, 32, 32)]
    [InlineData(64, 64, 64)]
    public void ClampAckBlocks_DoesNotExceedWindow(
        int requested,
        int windowBlocks,
        int expected)
    {
        Assert.Equal(expected, TransferTuning.ClampAckBlocks(requested, windowBlocks));
    }

    [Theory]
    [InlineData(-1L, 0)]
    [InlineData(0L, 0)]
    [InlineData(64L * 1024, 64 * 1024)]
    [InlineData(16L * 1024 * 1024, 16 * 1024 * 1024)]
    [InlineData(64L * 1024 * 1024, TransferTuning.MaxSocketBufferBytes)]
    public void ClampSocketBufferBytes_KeepsValueInsideSafetyLimit(
        long requested,
        int expected)
    {
        Assert.Equal(expected, TransferTuning.ClampSocketBufferBytes(requested));
    }

    [Fact]
    public void DefaultsMatchProtocolConstantsWhenDiagnosticsDisabled()
    {
        if (TransferTuning.IsDiagnostic)
        {
            // 诊断模式由进程环境变量决定，测试进程被显式设值时跳过。
            return;
        }

        Assert.Equal(
            FileTransferProtocol.DefaultWindowSize,
            TransferTuning.WindowBlocks);
        Assert.Equal(
            FileTransferProtocol.DefaultWindowAckInterval,
            TransferTuning.AckBlocks);
        Assert.Equal(0, TransferTuning.SocketBufferBytes);
    }

    [Fact]
    public void Describe_包含实际生效参数()
    {
        var description = TransferTuning.Describe();

        Assert.Contains($"window={TransferTuning.WindowBlocks}", description);
        Assert.Contains($"ackEvery={TransferTuning.AckBlocks}", description);
        Assert.Contains(
            $"socketBuffer={TransferTuning.SocketBufferBytes}",
            description);
    }
}

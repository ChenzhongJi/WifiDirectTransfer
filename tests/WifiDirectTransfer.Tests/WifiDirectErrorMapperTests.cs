using WifiDirectTransfer.Core;
using WifiDirectTransfer.Windows;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class WifiDirectErrorMapperTests
{
    [Fact]
    public void GetUserMessage_ReturnsChineseMessage_ForEveryDefinedCode()
    {
        foreach (var errorCode in Enum.GetValues<WifiDirectErrorCode>())
        {
            var message = WifiDirectErrorMapper.GetUserMessage(errorCode);

            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.Matches(@"[\u4e00-\u9fff]", message);
        }
    }

    [Theory]
    [InlineData(
        WifiDirectErrorCode.WifiDirectNotSupported,
        "当前设备不支持 Wi-Fi Direct 发现，请检查无线网卡或驱动。")]
    [InlineData(
        WifiDirectErrorCode.BrowseDiscoveryUnsupported,
        "当前设备不支持 Wi-Fi Direct 发现，请检查无线网卡或驱动。")]
    [InlineData(
        WifiDirectErrorCode.DiscoveryTimeout,
        "搜索设备超时，请确认两台电脑都已打开应用并开启 Wi-Fi；如果刚拒绝过 Windows 网络/防火墙提示，请允许后重试。")]
    [InlineData(
        WifiDirectErrorCode.ConnectionRejected,
        "对方拒绝或未在时限内接受连接。")]
    [InlineData(
        WifiDirectErrorCode.ConnectionTimeout,
        "对方拒绝或未在时限内接受连接；如果刚拒绝过 Windows 网络/防火墙提示，请允许后重试。")]
    [InlineData(
        WifiDirectErrorCode.ConnectionLost,
        "连接已断开，文件传输未完成。")]
    [InlineData(
        WifiDirectErrorCode.AccessDenied,
        "Windows 拒绝了网络访问。请重新连接并在系统弹窗中选择“允许”；如果已拒绝，请在 Windows 安全中心 → 防火墙和网络保护 → 允许应用通过防火墙中允许 WifiDirectTransfer.App 的专用网络访问。")]
    [InlineData(
        WifiDirectErrorCode.PeerNotFound,
        "设备已不在当前扫描结果中。请确认对端正在等待连接，重新扫描后选择标有“已发现”的设备；标有“未发现”的历史设备无法直接连接。")]
    [InlineData(
        WifiDirectErrorCode.ConnectionFailed,
        "无法建立 Wi-Fi Direct 连接，请重试；如果系统刚弹出网络/防火墙同意框，请允许后重试。")]
    [InlineData(
        WifiDirectErrorCode.StorageInsufficient,
        "接收目录空间不足。")]
    [InlineData(
        WifiDirectErrorCode.ChecksumMismatch,
        "文件校验失败，请重新发送。")]
    [InlineData(
        WifiDirectErrorCode.TransferInterrupted,
        "传输未完成，连接提前关闭。")]
    [InlineData(
        WifiDirectErrorCode.Unknown,
        "发生未知错误，请重试或查看日志。")]
    public void GetUserMessage_UsesC5Messages(
        WifiDirectErrorCode errorCode,
        string expected)
    {
        Assert.Equal(expected, WifiDirectErrorMapper.GetUserMessage(errorCode));
    }

    [Fact]
    public void GetUserMessage_Unknown_UsesLogHint()
    {
        Assert.Equal(
            "发生未知错误，请重试或查看日志。",
            WifiDirectErrorMapper.GetUserMessage(WifiDirectErrorCode.Unknown));
    }

    [Fact]
    public void Map_DomainException_UsesDomainCode()
    {
        Assert.Equal(
            WifiDirectErrorCode.DiscoveryTimeout,
            WifiDirectErrorMapper.Map(new DiscoveryTimeoutException()));
        Assert.Equal(
            WifiDirectErrorCode.ConnectionRejected,
            WifiDirectErrorMapper.Map(new ConnectionRejectedException()));
        Assert.Equal(
            WifiDirectErrorCode.BrowseDiscoveryUnsupported,
            WifiDirectErrorMapper.Map(
                new WifiDirectUnsupportedException(
                    "不支持发现",
                    WifiDirectErrorCode.BrowseDiscoveryUnsupported)));
    }

    [Fact]
    public void Map_UserCancellation_ReturnsCanceled()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        var errorCode = WifiDirectErrorMapper.Map(
            new TaskCanceledException(),
            WifiDirectOperation.Discovery,
            WifiDirectErrorContext.WinRt,
            cancellationTokenSource.Token);

        Assert.Equal(WifiDirectErrorCode.Canceled, errorCode);
    }

    [Theory]
    [InlineData(WifiDirectOperation.Discovery, WifiDirectErrorCode.DiscoveryTimeout)]
    [InlineData(WifiDirectOperation.Connection, WifiDirectErrorCode.ConnectionTimeout)]
    [InlineData(WifiDirectOperation.Transfer, WifiDirectErrorCode.TransferTimeout)]
    public void Map_NonUserCancellation_MapsToOperationTimeout(
        WifiDirectOperation operation,
        WifiDirectErrorCode expected)
    {
        var errorCode = WifiDirectErrorMapper.Map(
            new OperationCanceledException(),
            operation,
            WifiDirectErrorContext.WinRt);

        Assert.Equal(expected, errorCode);
    }

    [Fact]
    public void Map_UnauthorizedAccess_UsesContext()
    {
        Assert.Equal(
            WifiDirectErrorCode.PermissionDenied,
            WifiDirectErrorMapper.Map(
                new UnauthorizedAccessException(),
                WifiDirectOperation.Transfer,
                WifiDirectErrorContext.FileIo));

        Assert.Equal(
            WifiDirectErrorCode.AccessDenied,
            WifiDirectErrorMapper.Map(
                new UnauthorizedAccessException(),
                WifiDirectOperation.Connection,
                WifiDirectErrorContext.WinRt));
    }

    [Theory]
    [InlineData(WifiDirectOperation.Discovery, WifiDirectErrorCode.DiscoveryTimeout)]
    [InlineData(WifiDirectOperation.Connection, WifiDirectErrorCode.ConnectionTimeout)]
    [InlineData(WifiDirectOperation.Transfer, WifiDirectErrorCode.TransferTimeout)]
    public void Map_SocketTimeoutHResult_MapsToOperationTimeout(
        WifiDirectOperation operation,
        WifiDirectErrorCode expected)
    {
        var exception = new Exception { HResult = unchecked((int)0x8007274C) };

        Assert.Equal(
            expected,
            WifiDirectErrorMapper.Map(exception, operation, WifiDirectErrorContext.WinRt));
    }

    [Fact]
    public void Map_ConnectionRefusedHResult_MapsToRejected()
    {
        var exception = new Exception { HResult = unchecked((int)0x8007274D) };

        Assert.Equal(
            WifiDirectErrorCode.ConnectionRejected,
            WifiDirectErrorMapper.Map(
                exception,
                WifiDirectOperation.Connection,
                WifiDirectErrorContext.WinRt));
    }

    [Fact]
    public void Map_DiskFullHResult_MapsToStorageInsufficient()
    {
        var exception = new Exception { HResult = unchecked((int)0x80070070) };

        Assert.Equal(
            WifiDirectErrorCode.StorageInsufficient,
            WifiDirectErrorMapper.Map(
                exception,
                WifiDirectOperation.Transfer,
                WifiDirectErrorContext.FileIo));

        var handleDiskFull = new Exception { HResult = unchecked((int)0x80070027) };

        Assert.Equal(
            WifiDirectErrorCode.StorageInsufficient,
            WifiDirectErrorMapper.Map(
                handleDiskFull,
                WifiDirectOperation.Transfer,
                WifiDirectErrorContext.FileIo));
    }

    [Fact]
    public void Map_AccessDeniedHResult_UsesContext()
    {
        var exception = new Exception { HResult = unchecked((int)0x80070005) };

        Assert.Equal(
            WifiDirectErrorCode.AccessDenied,
            WifiDirectErrorMapper.Map(
                exception,
                WifiDirectOperation.Connection,
                WifiDirectErrorContext.WinRt));

        Assert.Equal(
            WifiDirectErrorCode.PermissionDenied,
            WifiDirectErrorMapper.Map(
                exception,
                WifiDirectOperation.Transfer,
                WifiDirectErrorContext.FileIo));
    }

    [Fact]
    public void Map_UnknownHResult_ReturnsUnknown()
    {
        var exception = new Exception { HResult = unchecked((int)0x80001234) };

        Assert.Equal(
            WifiDirectErrorCode.Unknown,
            WifiDirectErrorMapper.Map(
                exception,
                WifiDirectOperation.Connection,
                WifiDirectErrorContext.WinRt));
    }
}

using WifiDirectTransfer.Core;
using Windows.Networking.Sockets;

namespace WifiDirectTransfer.Windows;

/// <summary>
/// 将 Wi-Fi Direct 错误统一映射为用户可读的中文提示。
/// </summary>
public static class WifiDirectErrorMapper
{
    public static string GetUserMessage(WifiDirectErrorCode errorCode) => errorCode switch
    {
        WifiDirectErrorCode.WifiDirectNotSupported =>
            "当前设备不支持 Wi-Fi Direct 发现，请检查无线网卡或驱动。",
        WifiDirectErrorCode.WifiDirectDisabled =>
            "Wi-Fi Direct 当前不可用，请检查无线网卡和 Wi-Fi 开关。",
        WifiDirectErrorCode.BrowseDiscoveryUnsupported =>
            "当前设备不支持 Wi-Fi Direct 发现，请检查无线网卡或驱动。",
        WifiDirectErrorCode.DiscoveryFailed => "搜索设备失败，请重试。",
        WifiDirectErrorCode.DiscoveryTimeout =>
            "搜索设备超时，请确认两台电脑都已打开应用并开启 Wi-Fi；" +
            "如果刚拒绝过 Windows 网络/防火墙提示，请允许后重试。",
        WifiDirectErrorCode.ConnectionRejected =>
            "对方拒绝或未在时限内接受连接。",
        WifiDirectErrorCode.ConnectionTimeout =>
            "已发现设备，但对方未在时限内接受连接。请确认对端仍处于等待状态；" +
            "如果对端显示“等待中”但无法连接，请在对端点“重新等待”或重启对端应用。" +
            "如果刚拒绝过 Windows 网络/防火墙提示，请允许后重试。",
        WifiDirectErrorCode.ConnectionLost =>
            "连接已断开，文件传输未完成。",
        WifiDirectErrorCode.TransferInterrupted =>
            "传输未完成，连接提前关闭。",
        WifiDirectErrorCode.ChecksumMismatch =>
            "文件校验失败，请重新发送。",
        WifiDirectErrorCode.StorageInsufficient => "接收目录空间不足。",
        WifiDirectErrorCode.AccessDenied =>
            "Windows 拒绝了网络访问。请重新连接并在系统弹窗中选择“允许”；" +
            "如果已拒绝，请在 Windows 安全中心 → 防火墙和网络保护 → " +
            "允许应用通过防火墙中允许 WifiDirectTransfer.App 的专用网络访问。",
        WifiDirectErrorCode.InvalidProtocolData =>
            "收到无法识别的数据，连接可能不可靠。",
        WifiDirectErrorCode.Canceled => "操作已取消。",
        WifiDirectErrorCode.PeerNotFound =>
            "设备已不在当前扫描结果中。请确认对端正在等待连接，" +
            "重新扫描后选择标有“已发现”的设备；标有“未发现”的历史设备无法直接连接。",
        WifiDirectErrorCode.ConnectionFailed =>
            "无法建立 Wi-Fi Direct 连接，请重试；" +
            "如果系统刚弹出网络/防火墙同意框，请允许后重试。",
        WifiDirectErrorCode.TransferTimeout => "传输超时，请重试。",
        WifiDirectErrorCode.PermissionDenied => "没有权限读取或写入目标文件。",
        WifiDirectErrorCode.InvalidState => "当前状态无法执行此操作，请稍后重试。",
        WifiDirectErrorCode.FileTooLarge => "文件超过允许的最大传输大小。",
        WifiDirectErrorCode.FileSkipped => "同名文件已跳过。",
        WifiDirectErrorCode.IdentityVerificationFailed =>
            "设备身份验证失败，连接已断开。请重新配对。",
        WifiDirectErrorCode.DeviceIdentityUnavailable =>
            "无法初始化本机设备身份，无法建立安全连接。请检查 Windows 用户密钥存储后重试。",
        _ => "发生未知错误，请重试或查看日志。"
    };

    /// <summary>
    /// 按领域异常、取消、权限、Socket/HRESULT 的顺序映射错误。
    /// </summary>
    public static WifiDirectErrorCode Map(
        Exception exception,
        WifiDirectOperation operation = WifiDirectOperation.None,
        WifiDirectErrorContext context = WifiDirectErrorContext.Unknown,
        CancellationToken userCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is AggregateException aggregate &&
            aggregate.InnerExceptions.Count == 1)
        {
            return Map(
                aggregate.InnerExceptions[0],
                operation,
                context,
                userCancellationToken);
        }

        if (exception is WifiDirectException domainException)
        {
            return domainException.ErrorCode;
        }

        if (exception is OperationCanceledException)
        {
            if (userCancellationToken.IsCancellationRequested)
            {
                return WifiDirectErrorCode.Canceled;
            }

            return MapTimeout(operation);
        }

        if (exception is UnauthorizedAccessException)
        {
            return context == WifiDirectErrorContext.FileIo
                ? WifiDirectErrorCode.PermissionDenied
                : WifiDirectErrorCode.AccessDenied;
        }

        switch (exception.HResult & 0xFFFF)
        {
            case 5:
                return context == WifiDirectErrorContext.FileIo
                    ? WifiDirectErrorCode.PermissionDenied
                    : WifiDirectErrorCode.AccessDenied;

            case 0x27:
            case 0x70:
                return WifiDirectErrorCode.StorageInsufficient;
        }

        var socketErrorStatus = GetSocketErrorStatus(exception.HResult);
        var mappedCode = MapSocketErrorStatus(socketErrorStatus, operation);

        if (mappedCode != WifiDirectErrorCode.Unknown)
        {
            return mappedCode;
        }

        WifiDirectErrorLog.WriteUnknown(exception, operation, context);
        return WifiDirectErrorCode.Unknown;
    }

    private static WifiDirectErrorCode MapTimeout(WifiDirectOperation operation) => operation switch
    {
        WifiDirectOperation.Discovery => WifiDirectErrorCode.DiscoveryTimeout,
        WifiDirectOperation.Connection => WifiDirectErrorCode.ConnectionTimeout,
        WifiDirectOperation.Transfer => WifiDirectErrorCode.TransferTimeout,
        _ => WifiDirectErrorCode.Canceled
    };

    private static WifiDirectErrorCode MapSocketErrorStatus(
        SocketErrorStatus status,
        WifiDirectOperation operation) => status switch
    {
        SocketErrorStatus.ConnectionTimedOut => MapTimeout(operation),
        SocketErrorStatus.ConnectionRefused => WifiDirectErrorCode.ConnectionRejected,
        SocketErrorStatus.OperationAborted => operation switch
        {
            WifiDirectOperation.Discovery => WifiDirectErrorCode.DiscoveryFailed,
            WifiDirectOperation.Connection => WifiDirectErrorCode.ConnectionLost,
            WifiDirectOperation.Transfer => WifiDirectErrorCode.TransferInterrupted,
            _ => WifiDirectErrorCode.Canceled
        },
        SocketErrorStatus.ConnectionResetByPeer
            or SocketErrorStatus.NetworkDroppedConnectionOnReset
            or SocketErrorStatus.SoftwareCausedConnectionAbort
            or SocketErrorStatus.NetworkIsDown
            or SocketErrorStatus.NetworkIsUnreachable
            or SocketErrorStatus.HostIsDown
            or SocketErrorStatus.UnreachableHost => MapConnectionInterruption(operation),
        SocketErrorStatus.HostNotFound
            or SocketErrorStatus.NonAuthoritativeHostNotFound
            or SocketErrorStatus.NoDataRecordOfRequestedType
            or SocketErrorStatus.ClassTypeNotFound => operation == WifiDirectOperation.Discovery
                ? WifiDirectErrorCode.DiscoveryFailed
                : WifiDirectErrorCode.ConnectionFailed,
        SocketErrorStatus.NoAddressesFound => WifiDirectErrorCode.ConnectionFailed,
        SocketErrorStatus.AddressFamilyNotSupported
            or SocketErrorStatus.SocketTypeNotSupported => WifiDirectErrorCode.WifiDirectNotSupported,
        SocketErrorStatus.AddressAlreadyInUse
            or SocketErrorStatus.CannotAssignRequestedAddress
            or SocketErrorStatus.TooManyOpenFiles => WifiDirectErrorCode.ConnectionFailed,
        SocketErrorStatus.MessageTooLong => WifiDirectErrorCode.InvalidProtocolData,
        SocketErrorStatus.HttpInvalidServerResponse => WifiDirectErrorCode.ConnectionFailed,
        _ => WifiDirectErrorCode.Unknown
    };

    private static WifiDirectErrorCode MapConnectionInterruption(
        WifiDirectOperation operation) => operation switch
    {
        WifiDirectOperation.Discovery => WifiDirectErrorCode.DiscoveryFailed,
        WifiDirectOperation.Transfer => WifiDirectErrorCode.TransferInterrupted,
        _ => WifiDirectErrorCode.ConnectionLost
    };

    private static SocketErrorStatus GetSocketErrorStatus(int hresult)
    {
        try
        {
            return SocketError.GetStatus(hresult);
        }
        catch (Exception)
        {
            return SocketErrorStatus.Unknown;
        }
    }
}

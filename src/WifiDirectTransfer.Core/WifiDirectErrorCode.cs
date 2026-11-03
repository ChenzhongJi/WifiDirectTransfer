namespace WifiDirectTransfer.Core;

/// <summary>
/// 可由 UI 层展示的 Wi-Fi Direct 错误类别。
/// </summary>
public enum WifiDirectErrorCode
{
    Unknown = 0,
    WifiDirectNotSupported = 1,
    WifiDirectDisabled = 2,
    BrowseDiscoveryUnsupported = 3,
    DiscoveryFailed = 4,
    DiscoveryTimeout = 5,
    ConnectionRejected = 6,
    ConnectionTimeout = 7,
    ConnectionLost = 8,
    TransferInterrupted = 9,
    ChecksumMismatch = 10,
    StorageInsufficient = 11,
    AccessDenied = 12,
    InvalidProtocolData = 13,
    Canceled = 14,
    PeerNotFound = 15,
    ConnectionFailed = 16,
    TransferTimeout = 17,
    PermissionDenied = 18,
    InvalidState = 19,
    FileTooLarge = 20,
    FileSkipped = 21
}

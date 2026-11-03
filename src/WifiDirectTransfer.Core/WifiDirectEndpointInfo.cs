namespace WifiDirectTransfer.Core;

/// <summary>
/// StreamSocket 的本地与远端端点信息，仅用于日志和 UI 验证。
/// </summary>
public sealed record WifiDirectEndpointInfo(
    string LocalHostName,
    string LocalPort,
    string RemoteHostName,
    string RemotePort);

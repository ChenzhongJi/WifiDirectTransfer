namespace WifiDirectTransfer.Core;

public interface IWifiDirectConnectionService : IAsyncDisposable
{
    WifiDirectState State { get; }

    /// <summary>
    /// 接收端收到连接请求时触发，可能来自后台线程，UI 需要自行调度到界面线程。
    /// 拒绝请求时不要调用 AcceptAsync。
    /// </summary>
    event EventHandler<WifiDirectConnectionRequestEventArgs>? ConnectionRequested;

    /// <summary>
    /// 设置显示名、启用 Wi-Fi Direct 并启动 PeerFinder。
    /// </summary>
    Task StartAsync(string displayName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 搜索设备。没有发现设备时返回空列表；超时抛出 <see cref="DiscoveryTimeoutException"/>。
    /// </summary>
    Task<IReadOnlyList<WifiDirectPeer>> FindPeersAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 连接此前由 <see cref="FindPeersAsync"/> 返回的设备快照。
    /// 历史设备不在当前会话缓存中时，实现可以执行一次短时定向发现。
    /// </summary>
    Task<IDuplexChannel> ConnectAsync(
        WifiDirectPeer peer,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 用户确认后接受连接请求。未调用此方法即视为不接收。
    /// </summary>
    Task<IDuplexChannel> AcceptAsync(
        IWifiDirectConnectionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取连接端点信息，仅用于日志和 UI 验证。
    /// </summary>
    WifiDirectEndpointInfo GetEndpointInfo(IDuplexChannel channel);

    Task StopAsync(CancellationToken cancellationToken = default);
}

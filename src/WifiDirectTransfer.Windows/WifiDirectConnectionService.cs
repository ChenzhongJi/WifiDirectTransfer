using WifiDirectTransfer.Core;
using Windows.Networking.Proximity;
using Windows.Networking.Sockets;

namespace WifiDirectTransfer.Windows;

/// <summary>
/// M0 连接栈：PeerFinder 负责发现和配对，StreamSocket 负责双向字节流。
/// </summary>
public sealed class WifiDirectConnectionService : IWifiDirectConnectionService
{
    private static readonly TimeSpan KnownPeerDiscoveryTimeout =
        TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, PeerInformation> _peers = new(StringComparer.Ordinal);
    private WifiDirectConnection? _activeConnection;
    private int _state = (int)WifiDirectState.Idle;
    private int _disposed;
    private volatile bool _started;
    private bool _requestHandlerAttached;

    public WifiDirectState State => (WifiDirectState)Volatile.Read(ref _state);

    public event EventHandler<WifiDirectConnectionRequestEventArgs>? ConnectionRequested;

    public Task StartAsync(string displayName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfDisposed();

            if (_started)
            {
                PeerFinder.DisplayName = displayName;
                return Task.CompletedTask;
            }

            SetState(WifiDirectState.Starting);

            try
            {
                PeerFinder.DisplayName = displayName;
                PeerFinder.AllowWiFiDirect = true;
                PeerFinder.AllowBluetooth = false;
                PeerFinder.AllowInfrastructure = false;

                if (!_requestHandlerAttached)
                {
                    PeerFinder.ConnectionRequested += OnConnectionRequested;
                    _requestHandlerAttached = true;
                }

                PeerFinder.Start();
                _started = true;
                SetState(WifiDirectState.Listening);
            }
            catch
            {
                DetachRequestHandler();
                SetState(WifiDirectState.Faulted);
                throw;
            }
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<WifiDirectPeer>> FindPeersAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "搜索超时时间必须大于零。");
        }

        EnsureStarted();
        EnsureBrowseSupported();

        SetState(WifiDirectState.Discovering);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        try
        {
            var peerInformationResults = await PeerFinder
                .FindAllPeersAsync()
                .AsTask(linkedCts.Token)
                .ConfigureAwait(false);

            ThrowIfDisposed();

            var snapshots = new List<WifiDirectPeer>(peerInformationResults.Count);
            var peerMap = new Dictionary<string, PeerInformation>(StringComparer.Ordinal);

            foreach (var peerInformation in peerInformationResults)
            {
                var id = string.IsNullOrWhiteSpace(peerInformation.Id)
                    ? Guid.NewGuid().ToString("N")
                    : peerInformation.Id;
                var displayName = string.IsNullOrWhiteSpace(peerInformation.DisplayName)
                    ? "未命名设备"
                    : peerInformation.DisplayName;

                snapshots.Add(new WifiDirectPeer(id, displayName));
                peerMap[id] = peerInformation;
            }

            lock (_gate)
            {
                _peers.Clear();
                foreach (var pair in peerMap)
                {
                    _peers[pair.Key] = pair.Value;
                }
            }

            return snapshots;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new DiscoveryTimeoutException();
        }
        finally
        {
            SetState(GetRestingState());
        }
    }

    public async Task<IDuplexChannel> ConnectAsync(
        WifiDirectPeer peer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);

        EnsureStarted();
        cancellationToken.ThrowIfCancellationRequested();

        var peerInformation = await ResolvePeerInformationAsync(
                peer,
                cancellationToken)
            .ConfigureAwait(false);

        SetState(WifiDirectState.Connecting);
        var connected = false;

        try
        {
            var socket = await PeerFinder
                .ConnectAsync(peerInformation)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            var channel = AttachConnection(socket);
            connected = true;
            return channel;
        }
        finally
        {
            if (!connected)
            {
                SetState(GetRestingState());
            }
        }
    }

    /// <summary>
    /// 优先使用本次会话已有的 PeerInformation；历史设备不在缓存中时，
    /// 做一次短时定向发现，避免 UI 再走完整扫描流程。
    /// </summary>
    private async Task<PeerInformation> ResolvePeerInformationAsync(
        WifiDirectPeer peer,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_peers.TryGetValue(peer.Id, out var cached))
            {
                return cached;
            }

            var cachedByName = FindCachedPeerByName(peer.DisplayName);

            if (cachedByName is not null)
            {
                return cachedByName;
            }
        }

        await FindPeersAsync(KnownPeerDiscoveryTimeout, cancellationToken)
            .ConfigureAwait(false);

        lock (_gate)
        {
            if (_peers.TryGetValue(peer.Id, out var discovered))
            {
                return discovered;
            }

            var discoveredByName = FindCachedPeerByName(peer.DisplayName);

            if (discoveredByName is not null)
            {
                return discoveredByName;
            }
        }

        throw new WifiDirectPeerNotFoundException(peer);
    }

    /// <summary>
    /// 在持有 _gate 的情况下按显示名称查找唯一对端。
    /// 某些驱动每次扫描都会更换 Id，只能用唯一显示名称回退。
    /// </summary>
    private PeerInformation? FindCachedPeerByName(string? displayName)
    {
        var matches = _peers.Values
            .Where(peerInformation =>
                PeerIdentityMatcher.IsSameDisplayName(
                    peerInformation.DisplayName,
                    displayName))
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }

    public async Task<IDuplexChannel> AcceptAsync(
        IWifiDirectConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EnsureStarted();
        cancellationToken.ThrowIfCancellationRequested();

        if (request is not WifiDirectConnectionRequest concreteRequest)
        {
            throw new ArgumentException(
                "该连接请求不是由当前 Wi-Fi Direct 连接服务创建的。",
                nameof(request));
        }

        if (!concreteRequest.TryBeginAccept())
        {
            throw new WifiDirectInvalidStateException("该连接请求已经处理过。");
        }

        SetState(WifiDirectState.Connecting);
        var accepted = false;

        try
        {
            var socket = await PeerFinder
                .ConnectAsync(concreteRequest.PeerInformation)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            var channel = AttachConnection(socket);
            accepted = true;
            return channel;
        }
        finally
        {
            if (!accepted)
            {
                SetState(GetRestingState());
            }
        }
    }

    public WifiDirectEndpointInfo GetEndpointInfo(IDuplexChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (channel is not WifiDirectConnection connection)
        {
            throw new ArgumentException(
                "该通道不是由 Wi-Fi Direct 连接服务创建的。",
                nameof(channel));
        }

        return connection.EndpointInfo;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        await StopCoreAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopCoreAsync().ConfigureAwait(false);
    }

    private async Task StopCoreAsync()
    {
        WifiDirectConnection? connection;

        lock (_gate)
        {
            if (_started)
            {
                SetState(WifiDirectState.Stopping);
                DetachRequestHandler();

                try
                {
                    PeerFinder.Stop();
                }
                catch (Exception exception)
                {
                    WifiDirectErrorLog.WriteUnknown(
                        exception,
                        WifiDirectOperation.None,
                        WifiDirectErrorContext.WinRt);
                }
                finally
                {
                    _started = false;
                }
            }

            _peers.Clear();
            connection = _activeConnection;
            _activeConnection = null;
            SetState(WifiDirectState.Idle);
        }

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private WifiDirectConnection AttachConnection(StreamSocket socket)
    {
        if (socket is null)
        {
            throw new WifiDirectConnectionFailedException();
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            socket.Dispose();
            throw new ObjectDisposedException(nameof(WifiDirectConnectionService));
        }

        if (!_started)
        {
            socket.Dispose();
            throw new WifiDirectInvalidStateException("Wi-Fi Direct 连接服务已停止。");
        }

        var connection = new WifiDirectConnection(socket, OnConnectionDisposed);

        lock (_gate)
        {
            _activeConnection = connection;
        }

        SetState(WifiDirectState.Connected);
        return connection;
    }

    private void OnConnectionDisposed(WifiDirectConnection connection)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_activeConnection, connection))
            {
                return;
            }

            _activeConnection = null;
            SetState(_started ? WifiDirectState.Listening : WifiDirectState.Idle);
        }
    }

    private void OnConnectionRequested(object sender, ConnectionRequestedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_started)
        {
            return;
        }

        var peerInformation = args.PeerInformation;
        if (peerInformation is null)
        {
            return;
        }

        var id = string.IsNullOrWhiteSpace(peerInformation.Id)
            ? Guid.NewGuid().ToString("N")
            : peerInformation.Id;
        var displayName = string.IsNullOrWhiteSpace(peerInformation.DisplayName)
            ? "未命名设备"
            : peerInformation.DisplayName;

        var peer = new WifiDirectPeer(id, displayName);

        lock (_gate)
        {
            _peers[id] = peerInformation;
        }

        var request = new WifiDirectConnectionRequest(peer, peerInformation, DateTimeOffset.Now);
        ConnectionRequested?.Invoke(this, new WifiDirectConnectionRequestEventArgs(request));
    }

    private static void EnsureBrowseSupported()
    {
        PeerDiscoveryTypes discoveryTypes;

        try
        {
            discoveryTypes = PeerFinder.SupportedDiscoveryTypes;
        }
        catch (Exception exception)
        {
            throw new WifiDirectUnsupportedException(
                WifiDirectErrorMapper.GetUserMessage(WifiDirectErrorCode.BrowseDiscoveryUnsupported),
                WifiDirectErrorCode.BrowseDiscoveryUnsupported,
                exception);
        }

        if ((discoveryTypes & PeerDiscoveryTypes.Browse) != PeerDiscoveryTypes.Browse)
        {
            throw new WifiDirectUnsupportedException(
                WifiDirectErrorMapper.GetUserMessage(WifiDirectErrorCode.BrowseDiscoveryUnsupported),
                WifiDirectErrorCode.BrowseDiscoveryUnsupported);
        }
    }

    private void EnsureStarted()
    {
        ThrowIfDisposed();

        if (!_started)
        {
            throw new WifiDirectInvalidStateException(
                "Wi-Fi Direct 尚未启动，请先启动连接服务。");
        }
    }

    private void DetachRequestHandler()
    {
        if (!_requestHandlerAttached)
        {
            return;
        }

        PeerFinder.ConnectionRequested -= OnConnectionRequested;
        _requestHandlerAttached = false;
    }

    private WifiDirectState GetRestingState()
    {
        lock (_gate)
        {
            if (!_started)
            {
                return WifiDirectState.Idle;
            }

            return _activeConnection is not null
                ? WifiDirectState.Connected
                : WifiDirectState.Listening;
        }
    }

    private void SetState(WifiDirectState state) => Volatile.Write(ref _state, (int)state);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(WifiDirectConnectionService));
        }
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WifiDirectTransfer.Core;
using WifiDirectTransfer.Windows;

namespace WifiDirectTransfer.App;

public partial class MainWindow : Window
{
    private enum AppRole
    {
        Receiver,
        Sender
    }

    private readonly WifiDirectConnectionService _service = new();
    private readonly ObservableCollection<PeerListItem> _peerItems = new();
    private readonly ObservableCollection<ReceivedFileItem> _receivedFiles = new();
    private readonly KnownDeviceStore _knownDeviceStore = new();
    private readonly Dictionary<string, KnownDevice> _knownDevices =
        new(StringComparer.Ordinal);
    private readonly List<WifiDirectPeer> _discoveredPeers = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly string _receiveDirectory;
    private readonly ReceiverPreferencesStore _receiverPreferencesStore = new();
    private readonly ClipboardSyncService _clipboardSyncService;
    private readonly TransferHistoryStore _historyStore;
    private string _testFileName = "wdt-test-1MiB.bin";
    private SameNamePolicy _sameNamePolicy = SameNamePolicy.AutoRename;
    private bool _isApplyingReceiverPreferences;
    private bool _clipboardSyncEnabled;
    private bool _clipboardConsentAcknowledged;
    private bool _isApplyingClipboardPreference;
    private bool _clipboardDisabledDropLogged;
    private HwndSource? _hwndSource;
    private Point _receivedDragStart;
    private ReceivedFileItem? _receivedDragItem;

    private readonly SemaphoreSlim _resetGate = new(1, 1);

    private WifiDirectAvailability? _capability;
    private AppRole _role = AppRole.Receiver;
    private IDuplexChannel? _channel;
    private MessageDispatcher? _dispatcher;
    private FileReceiver? _fileReceiver;
    private TransferQueueManager? _queueManager;
    private CancellationTokenSource? _queueCts;
    private Task? _queueTask;
    private CancellationTokenSource? _messageLoopCts;
    private Task? _messageLoopTask;
    private bool _initialized;
    private bool _serviceStarted;
    private bool _isScanning;
    private bool _isConnecting;
    private bool _isSending;
    private bool _isHandlingRequest;
    private string? _connectedPeerName;
    private bool _step0Passed;
    private bool _messageLayerReady;
    private volatile bool _isClosing;
    private bool _allowClose;
    private bool _isHiddenToTray;
    private TrayIconService? _trayIcon;

    public MainWindow()
    {
        _receiveDirectory = GetReceiveDirectory();

        InitializeComponent();

        _historyStore = new TransferHistoryStore(log: Log);

        _clipboardSyncService = new ClipboardSyncService(
            Dispatcher,
            Log,
            UpdateStatus,
            _lifetimeCts.Token);

        PeerList.ItemsSource = _peerItems;
        ReceivedFilesList.ItemsSource = _receivedFiles;
        _service.ConnectionRequested += OnConnectionRequested;
        Application.Current.SessionEnding += OnSessionEnding;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            _hwndSource = source;
            source.AddHook(WndProc);
            _clipboardSyncService.Attach(source.Handle);
        }
    }

    private IntPtr WndProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == ClipboardSyncService.WmClipboardUpdate)
        {
            _clipboardSyncService.OnClipboardUpdate();
        }

        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        Application.Current.SessionEnding -= OnSessionEnding;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _clipboardSyncService.Dispose();
        base.OnClosed(e);
    }

    private bool CapabilitySupported =>
        _capability is { IsSupported: true, IsBrowseSupported: true };

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        ReceiveDirectoryText.Text = $"接收目录：{_receiveDirectory}";
        StatusText.Text = "未连接";
        Log("M0 界面已启动。");
        Log($"接收目录：{_receiveDirectory}");
        Log($"日志文件：{AppLog.FilePath}");
        Log($"本端构建：{GetBuildInfo()}");
        InitializeTrayIcon();

        LoadKnownDevices();
        RebuildPeerList(Array.Empty<WifiDirectPeer>());
        Log($"历史设备：{_knownDevices.Count} 台。");

        ApplyReceiverPreferences();
        Log($"同名文件策略：{GetSameNamePolicyText(_sameNamePolicy)}");
        Log(_clipboardSyncEnabled ? "剪贴板同步：已启用。" : "剪贴板同步：已关闭。");

        CheckCapability();

        _role = AppRole.Receiver;
        RoleReceiverRadio.IsChecked = true;

        UpdateUi();

        _ = Dispatcher.BeginInvoke(
            new Action(ShowIncompleteTransfersIfAny),
            DispatcherPriority.Background);
    }

    private void CheckCapability()
    {
        try
        {
            _capability = new WifiDirectCapabilityChecker().Check();

            if (CapabilitySupported)
            {
                CapabilityDot.Fill = Brushes.SeaGreen;
                CapabilityText.Text = "Wi-Fi Direct 可用";
                Log("C1 预检通过：Wi-Fi Direct 发现能力可用。");
            }
            else
            {
                CapabilityDot.Fill = Brushes.Firebrick;
                CapabilityText.Text = _capability.UnsupportedReason
                                      ?? "当前设备不支持 Wi-Fi Direct。";
                StatusText.Text = "Wi-Fi Direct 不可用";
                Log($"C1 预检失败：{CapabilityText.Text}");
            }
        }
        catch (Exception exception)
        {
            _capability = WifiDirectAvailability.Unsupported(
                "无法完成 Wi-Fi Direct 能力预检。");
            CapabilityDot.Fill = Brushes.Firebrick;
            CapabilityText.Text = _capability.UnsupportedReason!;
            StatusText.Text = LogException(
                exception,
                WifiDirectOperation.None,
                WifiDirectErrorContext.WinRt,
                CancellationToken.None);
        }

        UpdateUi();
    }

    private void RoleRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _isClosing)
        {
            return;
        }

        _role = RoleSenderRadio.IsChecked == true
            ? AppRole.Sender
            : AppRole.Receiver;

        Log(
            "连接角色：" +
            (_role == AppRole.Sender ? "发起连接端" : "等待连接端"));

        if (_channel is null && CapabilitySupported)
        {
            StatusText.Text = _role == AppRole.Sender
                ? "发起端：点击扫描设备"
                : _serviceStarted
                    ? "等待连接…"
                    : "等待端：点击等待连接";
        }

        UpdateUi();
    }

    private void ApplyReceiverPreferences()
    {
        var preferences = _receiverPreferencesStore.Load();
        _sameNamePolicy = preferences.SameNamePolicy;
        _clipboardConsentAcknowledged =
            preferences.ClipboardConsentAcknowledged;
        _clipboardSyncEnabled =
            preferences.ClipboardSyncEnabled &&
            _clipboardConsentAcknowledged;

        _isApplyingReceiverPreferences = true;

        try
        {
            SameNamePolicyComboBox.SelectedIndex = _sameNamePolicy switch
            {
                SameNamePolicy.Overwrite => 1,
                SameNamePolicy.Skip => 2,
                _ => 0
            };
        }
        finally
        {
            _isApplyingReceiverPreferences = false;
        }

        _isApplyingClipboardPreference = true;

        try
        {
            ClipboardSyncCheckBox.IsChecked = _clipboardSyncEnabled;
        }
        finally
        {
            _isApplyingClipboardPreference = false;
        }

        _clipboardSyncService.SetEnabled(_clipboardSyncEnabled);
    }

    private void SameNamePolicyComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_initialized || _isApplyingReceiverPreferences || _isClosing)
        {
            return;
        }

        if (SameNamePolicyComboBox.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string tag ||
            !Enum.TryParse<SameNamePolicy>(tag, out var policy) ||
            !Enum.IsDefined(policy))
        {
            return;
        }

        _sameNamePolicy = policy;

        if (_fileReceiver is not null)
        {
            _fileReceiver.SameNamePolicy = policy;
        }

        var saved = SaveReceiverPreferences();

        Log(
            $"同名文件策略：{GetSameNamePolicyText(policy)}" +
            (saved ? string.Empty : "（保存失败）"));
    }

    private bool SaveReceiverPreferences() =>
        _receiverPreferencesStore.Save(
            new ReceiverPreferences
            {
                SameNamePolicy = _sameNamePolicy,
                ClipboardSyncEnabled = _clipboardSyncEnabled,
                ClipboardConsentAcknowledged = _clipboardConsentAcknowledged
            });

    private void ClipboardSyncCheckBox_Checked(
        object sender,
        RoutedEventArgs e)
    {
        if (!_initialized || _isApplyingClipboardPreference || _isClosing)
        {
            return;
        }

        if (!_clipboardConsentAcknowledged)
        {
            var dialog = new ClipboardSyncConfirmationWindow
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
            {
                _isApplyingClipboardPreference = true;

                try
                {
                    ClipboardSyncCheckBox.IsChecked = false;
                }
                finally
                {
                    _isApplyingClipboardPreference = false;
                }

                return;
            }

            _clipboardConsentAcknowledged = true;
        }

        _clipboardSyncEnabled = true;
        _clipboardDisabledDropLogged = false;
        _clipboardSyncService.SetEnabled(true);

        var saved = SaveReceiverPreferences();
        Log("剪贴板同步已启用。" + (saved ? string.Empty : "（保存失败）"));
    }

    private void ClipboardSyncCheckBox_Unchecked(
        object sender,
        RoutedEventArgs e)
    {
        if (!_initialized || _isApplyingClipboardPreference || _isClosing)
        {
            return;
        }

        _clipboardSyncEnabled = false;
        _clipboardSyncService.SetEnabled(false);

        var saved = SaveReceiverPreferences();
        Log("剪贴板同步已关闭。" + (saved ? string.Empty : "（保存失败）"));
    }

    private static string GetSameNamePolicyText(SameNamePolicy policy) => policy switch
    {
        SameNamePolicy.Overwrite => "覆盖",
        SameNamePolicy.Skip => "跳过",
        _ => "自动重命名"
    };

    private void LoadKnownDevices()
    {
        _knownDevices.Clear();

        foreach (var device in _knownDeviceStore.Load())
        {
            if (!string.IsNullOrWhiteSpace(device.Id))
            {
                _knownDevices[device.Id] = device;
            }
        }
    }

    private void RebuildPeerList(IReadOnlyList<WifiDirectPeer> discoveredPeers)
    {
        var discoveredSnapshot = discoveredPeers.ToArray();

        _discoveredPeers.Clear();
        _discoveredPeers.AddRange(discoveredSnapshot);

        var selectedId = (PeerList.SelectedItem as PeerListItem)?.Peer.Id;
        var items = new List<PeerListItem>();
        var addedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var known in _knownDevices.Values
                     .OrderByDescending(device => device.LastConnectedAt))
        {
            var discovered = discoveredSnapshot.FirstOrDefault(
                peer => string.Equals(peer.Id, known.Id, StringComparison.Ordinal));
            var peer = discovered ?? new WifiDirectPeer(known.Id, known.DisplayName);

            if (addedIds.Add(known.Id))
            {
                items.Add(new PeerListItem(peer, known));
            }
        }

        foreach (var peer in discoveredSnapshot)
        {
            if (addedIds.Add(peer.Id))
            {
                items.Add(new PeerListItem(peer, null));
            }
        }

        _peerItems.Clear();

        foreach (var item in items)
        {
            _peerItems.Add(item);
        }

        if (selectedId is not null)
        {
            var selected = _peerItems.FirstOrDefault(
                item => string.Equals(item.Peer.Id, selectedId, StringComparison.Ordinal));

            if (selected is not null)
            {
                PeerList.SelectedItem = selected;
            }
        }

        if (PeerList.SelectedItem is null && _peerItems.Count > 0)
        {
            PeerList.SelectedItem =
                _peerItems.FirstOrDefault(item => item.IsKnown) ?? _peerItems[0];
        }

        UpdateUi();
    }

    private void RecordKnownDevice(WifiDirectPeer peer)
    {
        if (string.IsNullOrWhiteSpace(peer.Id))
        {
            return;
        }

        _knownDevices[peer.Id] = new KnownDevice
        {
            Id = peer.Id,
            DisplayName = peer.DisplayName,
            LastConnectedAt = DateTimeOffset.Now
        };

        var index = _discoveredPeers.FindIndex(
            existing => string.Equals(existing.Id, peer.Id, StringComparison.Ordinal));

        if (index >= 0)
        {
            _discoveredPeers[index] = peer;
        }
        else
        {
            _discoveredPeers.Add(peer);
        }

        var saved = _knownDeviceStore.Save(_knownDevices.Values);

        Log(
            $"已记录最近连接设备：DisplayName={peer.DisplayName}, Id={peer.Id}" +
            (saved ? string.Empty : "（保存失败）"));

        RebuildPeerList(_discoveredPeers);
    }

    private async Task<bool> EnsureServiceStartedAsync()
    {
        if (!CapabilitySupported)
        {
            Log("Wi-Fi Direct 不可用，无法启动 PeerFinder。");
            return false;
        }

        if (_serviceStarted)
        {
            return true;
        }

        var displayName = GetLocalDisplayName();
        Log($"PeerFinder.Start 开始：DisplayName={displayName}");

        try
        {
            await _service.StartAsync(displayName, _lifetimeCts.Token);
            _serviceStarted = true;
            Log($"PeerFinder.Start 完成：state={_service.State}");
            return true;
        }
        catch (Exception exception)
        {
            _serviceStarted = false;
            var message = LogException(
                exception,
                WifiDirectOperation.None,
                WifiDirectErrorContext.WinRt,
                _lifetimeCts.Token);
            UpdateStatus(message);
            return false;
        }
    }

    private async void WaitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_role != AppRole.Receiver || _channel is not null || _isHandlingRequest)
        {
            return;
        }

        if (!await EnsureServiceStartedAsync())
        {
            return;
        }

        Log("等待端已就绪，等待连接发起端连接。");
        UpdateStatus("等待连接…");
        UpdateUi();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        await ScanForPeersAsync();
    }

    private async Task<bool> ScanForPeersAsync()
    {
        if (_role != AppRole.Sender || _channel is not null || _isScanning || _isClosing)
        {
            return false;
        }

        if (!await EnsureServiceStartedAsync())
        {
            return false;
        }

        _isScanning = true;
        UpdateUi();
        UpdateStatus("正在搜索设备…");
        Log("FindAllPeersAsync 开始：超时 10 秒。");

        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);

        try
        {
            var peers = await _service.FindPeersAsync(
                TimeSpan.FromSeconds(10),
                scanCts.Token);

            RebuildPeerList(peers);

            Log($"FindAllPeersAsync 返回 {peers.Count} 台设备。");

            foreach (var peer in peers)
            {
                Log($"  peer: DisplayName={peer.DisplayName}, Id={peer.Id}");
            }

            if (peers.Count == 0)
            {
                Log("未发现可用设备。");
                UpdateStatus(_knownDevices.Count > 0
                    ? "未发现新设备，可尝试快速重连"
                    : "未发现设备");
            }
            else
            {
                UpdateStatus($"发现 {peers.Count} 台设备");
            }

            return true;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // 窗口关闭时的正常取消。
            return false;
        }
        catch (Exception exception)
        {
            var message = LogException(
                exception,
                WifiDirectOperation.Discovery,
                WifiDirectErrorContext.WinRt,
                _lifetimeCts.Token);
            UpdateStatus(message);
            return false;
        }
        finally
        {
            _isScanning = false;
            UpdateUi();
        }
    }

    private void PeerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateUi();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_role != AppRole.Sender ||
            _channel is not null ||
            _isConnecting ||
            PeerList.SelectedItem is not PeerListItem selectedItem)
        {
            return;
        }

        var peer = selectedItem.Peer;

        if (!await EnsureServiceStartedAsync())
        {
            return;
        }

        _isConnecting = true;
        UpdateUi();
        UpdateStatus($"正在连接 {peer.DisplayName}…");
        Log($"ConnectAsync 开始：DisplayName={peer.DisplayName}, Id={peer.Id}");

        IDuplexChannel? channel = null;
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        connectCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            channel = await _service.ConnectAsync(peer, connectCts.Token);
            AttachChannel(channel);
            Log($"ConnectAsync 成功：state={_service.State}");

            UpdateStatus("正在执行 Step 0 1 字节回显…");
            var step0Passed = await RunSenderStep0Async(channel, _lifetimeCts.Token);
            _step0Passed = step0Passed;

            if (!step0Passed)
            {
                await ResetConnectionAsync();
                return;
            }

            if (!await StartMessageLayerAsync(channel))
            {
                return;
            }

            RecordKnownDevice(peer);
            _connectedPeerName = peer.DisplayName;
            UpdateStatus("已连接，双方都可以发送文件");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // 窗口关闭时的正常取消。
        }
        catch (Exception exception)
        {
            var message = LogException(
                exception,
                WifiDirectOperation.Connection,
                WifiDirectErrorContext.WinRt,
                _lifetimeCts.Token);

            UpdateStatus(message);

            if (channel is not null)
            {
                await ResetConnectionAsync();

                if (!_isClosing)
                {
                    UpdateStatus(message);
                }
            }
        }
        finally
        {
            _isConnecting = false;
            UpdateUi();
        }
    }

    private async void QuickReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_role != AppRole.Sender ||
            _channel is not null ||
            _isScanning ||
            _isConnecting ||
            _isClosing ||
            PeerList.SelectedItem is not PeerListItem { IsKnown: true } selectedItem)
        {
            return;
        }

        var peer = selectedItem.Peer;

        if (!await EnsureServiceStartedAsync())
        {
            return;
        }

        _isConnecting = true;
        UpdateUi();
        UpdateStatus($"正在快速重连 {peer.DisplayName}…");
        Log($"快速重连开始：DisplayName={peer.DisplayName}, Id={peer.Id}");

        IDuplexChannel? channel = null;
        var fallbackToScan = false;

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        connectCts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            channel = await _service.ConnectAsync(peer, connectCts.Token);
            AttachChannel(channel);
            Log($"快速重连 ConnectAsync 成功：state={_service.State}");

            UpdateStatus("正在执行 Step 0 1 字节回显…");
            var step0Passed = await RunSenderStep0Async(channel, _lifetimeCts.Token);
            _step0Passed = step0Passed;

            if (!step0Passed)
            {
                await ResetConnectionAsync();
                fallbackToScan = true;
            }
            else if (await StartMessageLayerAsync(channel))
            {
                RecordKnownDevice(peer);
                _connectedPeerName = peer.DisplayName;
                UpdateStatus("已连接，双方都可以发送文件");
                Log("快速重连成功。");
            }
            else
            {
                fallbackToScan = true;
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // 窗口关闭时的正常取消。
        }
        catch (Exception exception)
        {
            var message = LogException(
                exception,
                WifiDirectOperation.Connection,
                WifiDirectErrorContext.WinRt,
                _lifetimeCts.Token);

            UpdateStatus($"{message} 正在回退到扫描…");
            Log("快速重连失败，回退到正常扫描流程。");
            fallbackToScan = true;
        }
        finally
        {
            _isConnecting = false;
            UpdateUi();
        }

        if (!fallbackToScan || _isClosing)
        {
            return;
        }

        if (channel is not null && _channel is not null)
        {
            await ResetConnectionAsync();
        }

        UpdateStatus("快速重连失败，正在重新扫描…");
        await ScanForPeersAsync();
    }

    private void OnConnectionRequested(object? sender, WifiDirectConnectionRequestEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnConnectionRequested(sender, e)));
            return;
        }

        _ = HandleConnectionRequestAsync(e.Request);
    }

    private async Task HandleConnectionRequestAsync(IWifiDirectConnectionRequest request)
    {
        if (_isClosing)
        {
            return;
        }

        if (_role != AppRole.Receiver)
        {
            Log($"忽略连接请求：当前不是等待连接端。DisplayName={request.Peer.DisplayName}, Id={request.Peer.Id}");
            return;
        }

        if (_channel is not null)
        {
            Log("已有活动连接，忽略新的连接请求。");
            return;
        }

        if (_isHandlingRequest)
        {
            Log("正在处理其他连接请求，忽略新的请求。");
            return;
        }

        _isHandlingRequest = true;
        UpdateUi();
        Log($"收到连接请求：DisplayName={request.Peer.DisplayName}, Id={request.Peer.Id}");

        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            ShowFromTray();
            _trayIcon?.ShowBalloon(
                "连接请求",
                $"来自 {request.Peer.DisplayName} 的 Wi-Fi Direct 连接请求。");
        }

        try
        {
            var dialog = new ReceiveConfirmationWindow(request.Peer)
            {
                Owner = this
            };

            var accepted = dialog.ShowDialog() == true;

            if (!accepted)
            {
                Log(dialog.TimedOut
                    ? "接收确认超时，已自动拒绝。"
                    : "用户拒绝了连接请求。");
                UpdateStatus("已拒绝，继续等待连接…");
                return;
            }

            Log("用户确认接收，开始 AcceptAsync。");
            UpdateStatus("正在接受连接…");

            using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            acceptCts.CancelAfter(TimeSpan.FromSeconds(30));

            var channel = await _service.AcceptAsync(request, acceptCts.Token);
            AttachChannel(channel);
            Log($"AcceptAsync 成功：state={_service.State}");

            UpdateStatus("正在执行 Step 0 1 字节回显…");
            var step0Passed = await RunReceiverStep0Async(channel, _lifetimeCts.Token);
            _step0Passed = step0Passed;

            if (!step0Passed)
            {
                await ResetConnectionAsync();
                return;
            }

            if (!await StartMessageLayerAsync(channel))
            {
                return;
            }

            RecordKnownDevice(request.Peer);
            _connectedPeerName = request.Peer.DisplayName;
            UpdateStatus("已连接，双方都可以发送文件");
            Log("连接已建立，双方都可以发送文件。");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // 窗口关闭时的正常取消。
        }
        catch (Exception exception)
        {
            var message = LogException(
                exception,
                WifiDirectOperation.Connection,
                WifiDirectErrorContext.WinRt,
                _lifetimeCts.Token);

            UpdateStatus(message);

            if (_channel is not null)
            {
                await ResetConnectionAsync();

                if (!_isClosing)
                {
                    UpdateStatus(message);
                }
            }
        }
        finally
        {
            _isHandlingRequest = false;
            UpdateUi();
        }
    }

    private void AttachChannel(IDuplexChannel channel)
    {
        _channel = channel;
        _step0Passed = false;
        _messageLayerReady = false;
        Log("连接已建立。");
        LogEndpointInfo(channel);
        UpdateUi();
    }

    private void LogEndpointInfo(IDuplexChannel channel)
    {
        try
        {
            var endpoint = _service.GetEndpointInfo(channel);

            Log(
                "StreamSocket.Information：" +
                $"LocalHostName={endpoint.LocalHostName}, " +
                $"LocalPort={endpoint.LocalPort}, " +
                $"RemoteHostName={endpoint.RemoteHostName}, " +
                $"RemotePort={endpoint.RemotePort}");
        }
        catch (Exception exception)
        {
            LogException(
                exception,
                WifiDirectOperation.Connection,
                WifiDirectErrorContext.WinRt,
                _lifetimeCts.Token);
        }
    }

    private async Task<bool> RunSenderStep0Async(
        IDuplexChannel channel,
        CancellationToken lifetimeToken)
    {
        var peers = _peerItems.Count;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        var payload = new byte[] { 0xA5 };

        try
        {
            await channel.Output.WriteAsync(payload, timeoutCts.Token);
            await channel.Output.FlushAsync(timeoutCts.Token);
            Log($"Step 0：已发送 1 字节（0x{payload[0]:X2}），等待回显。");

            var echo = new byte[1];
            await channel.Input.ReadExactlyAsync(echo, timeoutCts.Token);

            var success = echo[0] == payload[0];

            LogRaw(
                $"STEP0: peers={peers}, " +
                $"connected={success.ToString().ToLowerInvariant()}, " +
                $"bytes={(success ? 1 : 0)}/1");

            if (!success)
            {
                Log($"Step 0：回显字节不匹配，收到 0x{echo[0]:X2}。");
            }

            return success;
        }
        catch (Exception exception)
        {
            LogRaw($"STEP0: peers={peers}, connected=false, bytes=0/1");

            if (!_isClosing)
            {
                LogException(
                    exception,
                    WifiDirectOperation.Connection,
                    WifiDirectErrorContext.WinRt,
                    lifetimeToken);
            }

            return false;
        }
    }

    private async Task<bool> RunReceiverStep0Async(
        IDuplexChannel channel,
        CancellationToken lifetimeToken)
    {
        var peers = _peerItems.Count;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var buffer = new byte[1];
            await channel.Input.ReadExactlyAsync(buffer, timeoutCts.Token);
            await channel.Output.WriteAsync(buffer, timeoutCts.Token);
            await channel.Output.FlushAsync(timeoutCts.Token);

            LogRaw($"STEP0: peers={peers}, connected=true, bytes=1/1");
            Log($"Step 0：收到 0x{buffer[0]:X2} 并已回显。");

            return true;
        }
        catch (Exception exception)
        {
            LogRaw($"STEP0: peers={peers}, connected=false, bytes=0/1");

            if (!_isClosing)
            {
                LogException(
                    exception,
                    WifiDirectOperation.Connection,
                    WifiDirectErrorContext.WinRt,
                    lifetimeToken);
            }

            return false;
        }
    }

    private async Task<bool> StartMessageLayerAsync(IDuplexChannel channel)
    {
        if (_dispatcher is not null)
        {
            throw new InvalidOperationException("消息帧层已经启动。");
        }

        var dispatcher = new MessageDispatcher(
            channel.Input,
            channel.Output,
            ClipboardMessageCodec.MaxPayloadLength);
        _dispatcher = dispatcher;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _messageLoopCts = cts;

        // 先创建握手对象再启动接收循环，避免漏掉对端的 Hello。
        using var handshake = new ConnectionHandshake(
            dispatcher,
            GetBuildId(),
            ClipboardMessageCodec.MaxPayloadLength,
            TimeSpan.FromSeconds(10));

        _messageLoopTask = dispatcher.StartReceiveLoopAsync(cts.Token);

        UpdateStatus("正在协商协议版本…");
        Log(
            $"开始协议协商：localProtocol={MessageProtocol.ProtocolVersion}，" +
            $"localBuild={GetBuildId()}。");

        HelloMessage peerHello;

        try
        {
            peerHello = await handshake.ExchangeAsync(_lifetimeCts.Token);
        }
        catch (HandshakeTimeoutException exception)
        {
            Log($"ERROR: {exception.Message}");
            await ResetConnectionAsync();
            UpdateStatus(exception.Message);
            return false;
        }
        catch (ProtocolVersionMismatchException exception)
        {
            Log($"ERROR: {exception.Message}");
            await ResetConnectionAsync();
            UpdateStatus(exception.Message);
            return false;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            var message = LogException(
                exception,
                WifiDirectOperation.Connection,
                WifiDirectErrorContext.WinRt,
                _lifetimeCts.Token);
            await ResetConnectionAsync();
            UpdateStatus(message);
            return false;
        }

        Log(
            $"协议协商成功：peerProtocol={peerHello.ProtocolVersion}，" +
            $"peerBuild={peerHello.Build}，" +
            $"peerMaxPayload={peerHello.MaxPayloadLength}。");

        _clipboardSyncService.SetSender(
            (payload, token) =>
            {
                token.ThrowIfCancellationRequested();

                // 一旦开始写入帧就不能中途取消，否则会留下半个帧并损坏连接。
                return dispatcher.SendAsync(
                    MessageType.ClipboardUpdate,
                    payload,
                    CancellationToken.None);
            });

        var receiver = new FileReceiver(
            dispatcher,
            _receiveDirectory,
            progress: new Progress<FileTransferProgress>(value =>
                UpdateStatus($"正在接收：{value.BytesTransferred}/{value.TotalBytes} 字节")),
            sameNamePolicy: _sameNamePolicy,
            log: Log);

        receiver.FileStarted += OnFileStarted;
        receiver.FileReceived += OnFileReceived;
        receiver.FileFailed += OnFileFailed;
        receiver.FileSkipped += OnFileSkipped;
        _fileReceiver = receiver;

        var queue = new TransferQueueManager(
            dispatcher,
            errorMessageFactory: CreateQueueErrorMessage,
            log: Log);

        queue.FileStarted += OnQueueFileStarted;
        queue.FileCompleted += OnQueueFileCompleted;
        queue.FileFailed += OnQueueFileFailed;
        queue.FileSkipped += OnQueueFileSkipped;
        queue.QueueCompleted += OnQueueCompleted;
        _queueManager = queue;
        QueuePanel.DataContext = queue;

        dispatcher.MessageReceived += OnDispatcherMessageAsync;
        dispatcher.Disconnected += OnDispatcherDisconnected;

        _messageLayerReady = true;
        UpdateUi();

        Log(
            $"双向传输层已就绪：role={_role}，" +
            $"maxPayload={dispatcher.MaxPayloadLength}，" +
            $"peerMaxPayload={peerHello.MaxPayloadLength}，" +
            $"clipboardSync={_clipboardSyncEnabled}。");

        return true;
    }

    private void OnFileStarted(object? sender, FileStartedEventArgs args)
    {
        if (_isClosing || !ReferenceEquals(_fileReceiver, sender))
        {
            return;
        }

        var info = args.StartInfo;

        Log(info.IsResume
            ? $"接收：从断点继续，偏移 {info.StartOffset} 字节"
            : "接收：从头开始");

        UpdateStatus(info.IsResume
            ? $"正在接收（从 {info.StartOffset} 字节继续）…"
            : "正在接收…");
    }

    private void OnFileReceived(object? sender, FileReceivedEventArgs args)
    {
        if (_isClosing || !ReferenceEquals(_fileReceiver, sender))
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                new Action(() => OnFileReceived(sender, args)));
            return;
        }

        var result = args.Result;

        Log(
            "接收完成：" +
            $"file={result.FileName}, " +
            $"bytes={result.FileSize}, " +
            $"sha256={Convert.ToHexString(result.Sha256)}, " +
            $"elapsed={args.Elapsed.TotalMilliseconds:F0}ms, " +
            $"path={result.FilePath}");

        _receivedFiles.Insert(
            0,
            new ReceivedFileItem(result, DateTimeOffset.Now));
        ReceivedFilesEmptyText.Visibility = Visibility.Collapsed;

        UpdateStatus("已连接，可以继续传输");
        NotifyIfInBackground(
            "接收完成",
            $"{result.FileName}（{FormatBytes(result.FileSize)}）");

        RecordHistory(
            TransferDirection.Receive,
            result.FileName,
            result.FileSize,
            TransferOutcome.Completed,
            result.FilePath);
    }

    private void OnFileFailed(object? sender, FileFailedEventArgs args)
    {
        if (_isClosing || !ReferenceEquals(_fileReceiver, sender))
        {
            return;
        }

        if (args.Error is InsufficientStorageException storage)
        {
            var message = storage.UserMessage;

            Log(
                $"ERROR: code={WifiDirectErrorCode.StorageInsufficient}, " +
                $"message={message}");
            UpdateStatus(message);
            NotifyIfInBackground("接收失败", message, isError: true);
            RecordHistory(
                TransferDirection.Receive,
                args.FileName ?? "未知文件",
                0,
                TransferOutcome.Failed,
                null,
                message);
            return;
        }

        var errorContext = args.Error is COMException
            ? WifiDirectErrorContext.WinRt
            : WifiDirectErrorContext.FileIo;
        var errorMessage = LogException(
            args.Error,
            WifiDirectOperation.Transfer,
            errorContext,
            _lifetimeCts.Token);

        UpdateStatus(errorMessage);
        NotifyIfInBackground("接收失败", errorMessage, isError: true);
        RecordHistory(
            TransferDirection.Receive,
            args.FileName ?? "未知文件",
            0,
            TransferOutcome.Failed,
            null,
            errorMessage);
    }

    private void OnFileSkipped(object? sender, FileSkippedEventArgs args)
    {
        if (_isClosing || !ReferenceEquals(_fileReceiver, sender))
        {
            return;
        }

        Log($"同名文件已跳过：{args.FileName}");
        UpdateStatus($"已跳过同名文件：{args.FileName}");

        RecordHistory(
            TransferDirection.Receive,
            args.FileName,
            0,
            TransferOutcome.Skipped,
            null,
            "同名文件已跳过");
    }

    private void ReceivedFilesList_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _receivedDragStart = e.GetPosition(ReceivedFilesList);
        _receivedDragItem = GetReceivedFileItem(e.OriginalSource as DependencyObject);
    }

    private void ReceivedFilesList_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        _receivedDragItem = null;
    }

    private void ReceivedFilesList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _receivedDragItem is null)
        {
            return;
        }

        var position = e.GetPosition(ReceivedFilesList);

        if (Math.Abs(position.X - _receivedDragStart.X) <
                SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _receivedDragStart.Y) <
                SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var dragItem = _receivedDragItem;
        _receivedDragItem = null;

        var items = ReceivedFilesList.SelectedItems
            .OfType<ReceivedFileItem>()
            .ToList();

        if (!items.Contains(dragItem))
        {
            items = new List<ReceivedFileItem> { dragItem };
        }

        var paths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (File.Exists(item.FilePath) &&
                seenPaths.Add(item.FilePath))
            {
                paths.Add(item.FilePath);
            }
        }

        if (paths.Count == 0)
        {
            Log("接收文件已不存在，无法拖出。");
            UpdateStatus("接收文件已不存在");
            return;
        }

        // W3 折中：未实现 VirtualFileDataObject 的 CFSTR_FILEDESCRIPTOR /
        // CFSTR_FILECONTENTS，直接通过 FileDrop 传递接收目录中的真实文件路径。
        // 限制：拖出期间文件必须存在，目标程序会立即从磁盘读取；大文件没有
        // 虚拟文件的延迟内容读取能力，源文件被移动或删除时拖放会失败。
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths.ToArray());

        try
        {
            e.Handled = true;
            DragDrop.DoDragDrop(
                ReceivedFilesList,
                data,
                DragDropEffects.Copy);
        }
        catch (Exception exception)
        {
            Log($"拖出文件失败：{exception.GetType().Name}");
        }
    }

    private ReceivedFileItem? GetReceivedFileItem(DependencyObject? source)
    {
        if (source is null)
        {
            return null;
        }

        var container = ItemsControl.ContainerFromElement(
            ReceivedFilesList,
            source) as ListBoxItem;

        return container?.DataContext as ReceivedFileItem;
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (_isClosing)
        {
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] droppedPaths ||
            droppedPaths.Length == 0)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        if (_channel is null || !_step0Passed)
        {
            ShowDropPrompt("请先连接对端设备后再拖入文件");
            return;
        }

        if (_queueManager is null)
        {
            ShowDropPrompt("请先连接对端设备后再拖入文件");
            return;
        }

        if (_queueManager.IsRunning)
        {
            ShowDropPrompt("传输队列正在运行，请等待完成后再拖入文件。");
            return;
        }

        e.Effects = DragDropEffects.Copy;

        try
        {
            var files = DroppedFileCollector.Collect(droppedPaths, Log);

            if (files.Count == 0)
            {
                Log("拖入内容中没有可发送的文件。");
                UpdateStatus("拖入内容中没有可发送的文件");
                return;
            }

            var added = _queueManager.EnqueueFiles(files);
            Log($"拖入添加 {added.Count} 个文件到队列。");
            UpdateStatus($"已从拖入添加 {added.Count} 个文件");
            UpdateUi();
        }
        catch (InvalidOperationException exception)
        {
            Log($"拖入失败：{exception.Message}");
            ShowDropPrompt("传输队列正在运行，请等待完成后再拖入文件。");
        }
        catch (Exception exception)
        {
            var message = LogException(
                exception,
                WifiDirectOperation.Transfer,
                WifiDirectErrorContext.FileIo,
                _lifetimeCts.Token);
            UpdateStatus(message);
        }
    }

    private void ShowDropPrompt(string message)
    {
        Log(message);
        UpdateStatus(message);

        MessageBox.Show(
            this,
            message,
            "拖入发送",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnQueueFileStarted(object? sender, QueueFileStartedEventArgs args)
    {
        if (_isClosing)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                new Action(() => OnQueueFileStarted(sender, args)));
            return;
        }

        if (!ReferenceEquals(_queueManager, sender))
        {
            return;
        }

        Log(args.StartInfo.IsResume
            ? $"队列：{args.File.FileName} 从断点继续，偏移 {args.StartInfo.StartOffset} 字节"
            : $"队列：{args.File.FileName} 从头开始");

        UpdateStatus(args.StartInfo.IsResume
            ? $"正在发送 {args.File.FileName}（从 {args.StartInfo.StartOffset} 字节继续）…"
            : $"正在发送 {args.File.FileName}…");
    }

    private void OnQueueFileCompleted(object? sender, QueueFileCompletedEventArgs args)
    {
        if (_isClosing || !ReferenceEquals(_queueManager, sender))
        {
            return;
        }

        var result = args.Result;

        Log(
            "队列文件完成：" +
            $"file={result.FileName}, " +
            $"bytes={result.FileSize}, " +
            $"startOffset={result.StartOffset}, " +
            $"sha256={Convert.ToHexString(result.Sha256)}");

        RecordHistory(
            TransferDirection.Send,
            result.FileName,
            result.FileSize,
            TransferOutcome.Completed,
            args.File.SourcePath);
    }

    private void OnQueueFileFailed(object? sender, QueueFileFailedEventArgs args)
    {
        if (_isClosing || !ReferenceEquals(_queueManager, sender))
        {
            return;
        }

        var errorMessage = LogException(
            args.Error,
            WifiDirectOperation.Transfer,
            WifiDirectErrorContext.FileIo,
            _lifetimeCts.Token);

        Log(
            "队列文件失败：" +
            $"file={args.File.FileName}, " +
            $"message={args.File.ErrorMessage}");

        UpdateStatus(errorMessage);

        RecordHistory(
            TransferDirection.Send,
            args.File.FileName,
            args.File.Size,
            TransferOutcome.Failed,
            args.File.SourcePath,
            args.File.ErrorMessage ?? errorMessage);
    }

    private void OnQueueFileSkipped(object? sender, QueueFileSkippedEventArgs args)
    {
        if (_isClosing || !ReferenceEquals(_queueManager, sender))
        {
            return;
        }

        Log(
            "队列文件已跳过：" +
            $"file={args.File.FileName}, " +
            $"reason={args.Reason}");

        UpdateStatus($"已跳过同名文件：{args.File.FileName}");

        RecordHistory(
            TransferDirection.Send,
            args.File.FileName,
            args.File.Size,
            TransferOutcome.Skipped,
            args.File.SourcePath,
            args.Reason);
    }

    private void OnQueueCompleted(object? sender, TransferQueueCompletedEventArgs args)
    {
        if (_isClosing || !ReferenceEquals(_queueManager, sender))
        {
            return;
        }

        Log(
            "队列结束：" +
            $"total={args.TotalCount}, " +
            $"completed={args.CompletedCount}, " +
            $"failed={args.FailedCount}, " +
            $"skipped={args.SkippedCount}, " +
            $"canceled={args.WasCanceled}");

        UpdateStatus(args.WasCanceled
            ? "队列已取消"
            : args.FailedCount > 0
                ? "队列完成，部分文件失败"
                : args.SkippedCount > 0
                    ? $"队列完成，{args.SkippedCount} 个文件被跳过"
                    : "队列传输完成");

        NotifyIfInBackground(
            "发送队列",
            args.WasCanceled
                ? "队列已取消。"
                : args.FailedCount > 0
                    ? $"队列完成，{args.FailedCount} 个文件失败。"
                    : $"队列完成，成功 {args.CompletedCount} 个文件。",
            isError: args.FailedCount > 0);

        UpdateUi();
    }

    private static string CreateQueueErrorMessage(Exception exception)
    {
        if (exception is WifiDirectException)
        {
            return exception.Message;
        }

        if (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return "找不到源文件。";
        }

        if (exception is UnauthorizedAccessException)
        {
            return "没有权限读取源文件。";
        }

        if (exception is IOException)
        {
            return "读取源文件失败。";
        }

        if (exception is ArgumentException or NotSupportedException)
        {
            return "源文件路径无效。";
        }

        if (exception is InvalidOperationException)
        {
            return "当前连接状态无法继续传输，请重新连接。";
        }

        var errorCode = WifiDirectErrorMapper.Map(
            exception,
            WifiDirectOperation.Transfer,
            WifiDirectErrorContext.FileIo,
            CancellationToken.None);

        return WifiDirectErrorMapper.GetUserMessage(errorCode);
    }

    private async Task OnDispatcherMessageAsync(
        MessageDispatcher sender,
        MessageReceivedEventArgs args,
        CancellationToken cancellationToken)
    {
        if (_isClosing || !ReferenceEquals(_dispatcher, sender))
        {
            return;
        }

        try
        {
            switch (args.Type)
            {
                case MessageType.TextMessage:
                    Log($"文本消息：{TextMessageCodec.Decode(args.Payload.Span)}");
                    break;

                case MessageType.ControlMessage:
                {
                    var control = ControlMessageCodec.Deserialize(args.Payload.Span);
                    Log($"控制消息：{FormatControlMessage(control)}");
                    break;
                }

                case MessageType.ClipboardUpdate:
                    await HandleRemoteClipboardAsync(
                        args.Payload,
                        cancellationToken);
                    break;
            }
        }
        catch (Exception exception)
        {
            LogException(
                exception,
                WifiDirectOperation.Transfer,
                WifiDirectErrorContext.WinRt,
                _lifetimeCts.Token);
        }
    }

    private async void OnDispatcherDisconnected(object? sender, DisconnectedEventArgs args)
    {
        if (_isClosing)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => OnDispatcherDisconnected(sender, args)));
            return;
        }

        if (_isClosing || !ReferenceEquals(_dispatcher, sender))
        {
            return;
        }

        string message;

        if (args.RemoteClosed)
        {
            Log("消息连接已关闭（远端关闭）。");
            message = WifiDirectErrorMapper.GetUserMessage(
                WifiDirectErrorCode.ConnectionLost);
            LogErrorCode(WifiDirectErrorCode.ConnectionLost);
        }
        else if (args.Error is not null)
        {
            message = LogException(
                args.Error,
                WifiDirectOperation.Transfer,
                WifiDirectErrorContext.WinRt,
                _lifetimeCts.Token);
        }
        else
        {
            Log("消息连接已断开。");
            message = WifiDirectErrorMapper.GetUserMessage(
                WifiDirectErrorCode.ConnectionLost);
        }

        try
        {
            await ResetConnectionAsync();
        }
        catch (Exception)
        {
            // 连接已断开，重置失败不影响退出或后续重连。
        }

        if (!_isClosing)
        {
            UpdateStatus(message);
        }
    }

    private async Task HandleRemoteClipboardAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (!_clipboardSyncEnabled || _isClosing)
        {
            if (!_isClosing && !_clipboardDisabledDropLogged)
            {
                _clipboardDisabledDropLogged = true;
                Log(
                    "收到远端剪贴板数据，但本机剪贴板同步未开启，已忽略。");
            }

            return;
        }

        _clipboardDisabledDropLogged = false;

        await _clipboardSyncService
            .HandleRemotePayloadAsync(payload, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string FormatControlMessage(ControlMessage message) => message.Subtype switch
    {
        ControlMessageSubtype.Ack =>
            $"Ack, persistedOffset={message.Offset}",
        ControlMessageSubtype.ResumeRequest =>
            $"ResumeRequest, offset={message.Offset}",
        ControlMessageSubtype.TransferError =>
            $"TransferError, code={message.ErrorCode}, message={message.Message}",
        ControlMessageSubtype.QueueInfo =>
            $"QueueInfo, totalBytes={message.Offset}",
        ControlMessageSubtype.FileSkipped => "FileSkipped",
        _ => message.Subtype.ToString()
    };

    private void AddQueueFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSending ||
            _queueManager is not { IsRunning: false } queue)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择要发送的文件",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var added = queue.EnqueueFiles(dialog.FileNames);

            if (added.Count == 0)
            {
                return;
            }

            Log($"已添加 {added.Count} 个文件到队列。");
            UpdateStatus($"队列已添加 {added.Count} 个文件");
            UpdateUi();
        }
        catch (Exception exception)
        {
            var message = LogException(
                exception,
                WifiDirectOperation.Transfer,
                WifiDirectErrorContext.FileIo,
                _lifetimeCts.Token);
            UpdateStatus(message);
        }
    }

    private async void StartQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_channel is null ||
            _dispatcher is null ||
            !_step0Passed ||
            _isSending ||
            _queueManager is not { CanStart: true } queue)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _queueCts = cts;
        Task? queueTask = null;

        Log($"队列传输开始：total={queue.TotalCount}, waiting={queue.WaitingCount}");
        UpdateStatus("正在发送队列…");

        try
        {
            queueTask = queue.StartAsync(cts.Token);
            _queueTask = queueTask;
            UpdateUi();

            await queueTask;
        }
        catch (OperationCanceledException)
        {
            // 队列内部已处理取消并更新文件状态。
        }
        catch (Exception exception)
        {
            var message = LogException(
                exception,
                WifiDirectOperation.Transfer,
                WifiDirectErrorContext.FileIo,
                _lifetimeCts.Token);

            UpdateStatus(message);
        }
        finally
        {
            if (ReferenceEquals(_queueTask, queueTask))
            {
                _queueTask = null;
            }

            if (ReferenceEquals(_queueCts, cts))
            {
                _queueCts = null;
            }

            cts.Dispose();
            UpdateUi();
        }
    }

    private void ClearQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSending ||
            _queueManager is not { IsRunning: false } queue)
        {
            return;
        }

        try
        {
            queue.Clear();
            Log("队列已清空。");
            UpdateStatus("已连接，可以继续传输");
            UpdateUi();
        }
        catch (Exception exception)
        {
            var message = LogException(
                exception,
                WifiDirectOperation.Transfer,
                WifiDirectErrorContext.FileIo,
                _lifetimeCts.Token);
            UpdateStatus(message);
        }
    }

    private async void SendTestFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_channel is null ||
            _dispatcher is null ||
            !_messageLayerReady ||
            !_step0Passed ||
            _isSending ||
            _queueManager?.IsRunning == true)
        {
            return;
        }

        _isSending = true;
        UpdateUi();

        // 失败/断连时保留名称以便续传；成功后换名，避免与已存在文件冲突。
        var fileName = _testFileName;
        var payload = CreateTestPayload(1024 * 1024);

        using var source = new MemoryStream(payload);
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        sendCts.CancelAfter(TimeSpan.FromMinutes(2));

        var stopwatch = Stopwatch.StartNew();
        UpdateStatus("正在发送测试文件…");
        Log($"发送开始：file={fileName}, bytes={payload.Length}");

        try
        {
            var progress = new Progress<FileTransferProgress>(value =>
                UpdateStatus($"正在发送：{value.BytesTransferred}/{value.TotalBytes} 字节"));

            var startProgress = new Progress<FileTransferStartInfo>(info =>
                Log(info.IsResume
                    ? $"发送：从断点继续，偏移 {info.StartOffset} 字节"
                    : "发送：从头开始"));

            var result = await FileSender.SendAsync(
                _dispatcher,
                source,
                fileName,
                progress: progress,
                startProgress: startProgress,
                cancellationToken: sendCts.Token,
                log: Log);

            stopwatch.Stop();

            Log(
                "发送完成：" +
                $"file={result.FileName}, " +
                $"bytes={result.FileSize}, " +
                $"startOffset={result.StartOffset}, " +
                $"sha256={Convert.ToHexString(result.Sha256)}, " +
                $"elapsed={stopwatch.ElapsedMilliseconds}ms");

            _testFileName = $"wdt-test-{DateTime.Now:yyyyMMdd-HHmmss-fff}.bin";
            UpdateStatus("已连接，可以继续传输");
        }
        catch (FileSkippedException exception)
        {
            stopwatch.Stop();

            Log($"发送被跳过：file={fileName}, message={exception.Message}");
            _testFileName = $"wdt-test-{DateTime.Now:yyyyMMdd-HHmmss-fff}.bin";
            UpdateStatus("对端已跳过同名文件");
        }
        catch (RemoteTransferException exception)
            when (exception.ErrorCode == WifiDirectErrorCode.StorageInsufficient)
        {
            stopwatch.Stop();

            Log(
                $"ERROR: code={WifiDirectErrorCode.StorageInsufficient}, " +
                $"message={exception.Message}");
            UpdateStatus(exception.Message);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // 窗口关闭时的正常取消。
        }
        catch (Exception exception)
        {
            var message = LogException(
                exception,
                WifiDirectOperation.Transfer,
                WifiDirectErrorContext.WinRt,
                _lifetimeCts.Token);

            UpdateStatus(message);

            if (!_isClosing)
            {
                await ResetConnectionAsync();

                if (!_isClosing)
                {
                    UpdateStatus(message);
                }
            }
        }
        finally
        {
            _isSending = false;
            UpdateUi();
        }
    }

    private async Task ResetConnectionAsync()
    {
        await _resetGate.WaitAsync().ConfigureAwait(true);

        try
        {
            var channel = _channel;
            var dispatcher = _dispatcher;
            var fileReceiver = _fileReceiver;
            var queueManager = _queueManager;
            var queueCts = _queueCts;
            var queueTask = _queueTask;
            var loopCts = _messageLoopCts;
            var loopTask = _messageLoopTask;

            _channel = null;
            _dispatcher = null;
            _queueManager = null;
            _queueCts = null;
            _queueTask = null;
            _messageLoopCts = null;
            _messageLoopTask = null;
            _step0Passed = false;
            _messageLayerReady = false;
            QueuePanel.DataContext = null;
            _clipboardSyncService.SetSender(null);

            if (dispatcher is not null)
            {
                dispatcher.MessageReceived -= OnDispatcherMessageAsync;
                dispatcher.Disconnected -= OnDispatcherDisconnected;
            }

            if (queueManager is not null)
            {
                queueManager.FileStarted -= OnQueueFileStarted;
                queueManager.FileCompleted -= OnQueueFileCompleted;
                queueManager.FileFailed -= OnQueueFileFailed;
                queueManager.FileSkipped -= OnQueueFileSkipped;
                queueManager.QueueCompleted -= OnQueueCompleted;
            }

            try
            {
                queueCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 队列已经结束并释放了 CTS。
            }

            if (queueTask is not null)
            {
                try
                {
                    await queueTask.ConfigureAwait(true);
                }
                catch (Exception)
                {
                    // 队列内部已经处理取消和单个文件的失败。
                }
            }

            try
            {
                loopCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 接收循环已经结束并释放了 CTS。
            }

            if (channel is not null)
            {
                try
                {
                    await channel.DisposeAsync();
                }
                catch (Exception)
                {
                    // 连接可能已经断开，清理失败不应阻止重置。
                }
            }

            if (loopTask is not null)
            {
                try
                {
                    await loopTask;
                }
                catch (Exception)
                {
                    // StartReceiveLoopAsync 已经在内部处理断连异常。
                }
            }

            if (fileReceiver is not null)
            {
                await fileReceiver.DisposeAsync();

                if (ReferenceEquals(_fileReceiver, fileReceiver))
                {
                    _fileReceiver = null;
                }
            }

            loopCts?.Dispose();

            if (_isClosing)
            {
                return;
            }

            Log("连接已重置。");
            UpdateStatus(_role == AppRole.Sender
                ? "发起端：点击扫描设备"
                : _serviceStarted
                    ? "等待连接…"
                    : "等待端：点击等待连接");
            UpdateUi();
        }
        finally
        {
            _resetGate.Release();
        }
    }

    private void ShowIncompleteTransfersIfAny()
    {
        if (_isClosing)
        {
            return;
        }

        IReadOnlyList<IncompleteTransfer> transfers;

        try
        {
            transfers = IncompleteTransferScanner.Scan(_receiveDirectory);
        }
        catch (Exception exception)
        {
            Log($"扫描未完成传输失败：{exception.GetType().Name}");
            return;
        }

        if (transfers.Count == 0)
        {
            return;
        }

        Log($"发现 {transfers.Count} 个未完成传输。");

        var dialog = new IncompleteTransfersWindow(transfers)
        {
            Owner = this
        };

        dialog.ShowDialog();

        if (dialog.DeleteAll)
        {
            IncompleteTransferScanner.Delete(transfers);
            Log("已删除未完成传输文件。");
            UpdateStatus("已删除未完成传输文件");
        }
        else
        {
            Log("已保留未完成传输文件，等待续传。");
        }
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new TrayIconService();
        _trayIcon.ShowRequested += ShowFromTray;
        _trayIcon.ExitRequested += OnTrayExitRequested;
        Log("托盘图标已创建。");
    }

    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        // 系统注销/关机时不要弹出后台确认框。
        _allowClose = true;
    }

    private void OnTrayExitRequested()
    {
        Log("从托盘菜单退出应用。");
        _ = ShutdownAsync();
    }

    public void ShowFromTray()
    {
        if (_isClosing)
        {
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        ShowInTaskbar = true;
        Activate();
        Topmost = true;
        Topmost = false;
        _isHiddenToTray = false;
        Log("已从托盘恢复窗口。");
    }

    private void HideToTray()
    {
        Hide();
        _isHiddenToTray = true;
        Log("窗口已隐藏到托盘，传输继续在后台进行。");
        _trayIcon?.ShowBalloon(
            "后台运行中",
            "应用仍在后台保持连接和传输。双击托盘图标可恢复窗口。");
    }

    private bool HasBackgroundWork =>
        _queueManager is { IsRunning: true } ||
        _channel is not null ||
        (_serviceStarted && _role == AppRole.Receiver);

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;

        if (_isClosing)
        {
            return;
        }

        Log(
            "收到窗口关闭请求：" +
            $"queueRunning={_queueManager?.IsRunning}，" +
            $"connected={_channel is not null}，" +
            $"serviceStarted={_serviceStarted}，" +
            $"role={_role}。");
        if (HasBackgroundWork)
        {
            var dialog = new BackgroundCloseWindow
            {
                Owner = this
            };

            Log("显示后台关闭对话框。");
            dialog.ShowDialog();
            Log($"关闭选择：{dialog.Choice}。");

            if (dialog.Choice == BackgroundCloseChoice.Background)
            {
                HideToTray();
                return;
            }

            if (dialog.Choice != BackgroundCloseChoice.Exit)
            {
                return;
            }
        }

        await ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;

        try
        {
            _lifetimeCts.Cancel();
            await ResetConnectionAsync();

            _service.ConnectionRequested -= OnConnectionRequested;

            try
            {
                await _service.DisposeAsync();
            }
            catch (Exception)
            {
                // 关闭流程继续。
            }
        }
        catch (Exception)
        {
            // 关闭流程不应阻塞退出。
        }
        finally
        {
            _allowClose = true;

            // 不能在 Closing 事件仍处于活动状态时同步调用 Close，
            // 排队到当前 Closing 事件返回后再关闭应用。
            try
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        Application.Current.Shutdown();
                    }
                    catch (Exception)
                    {
                        // 应用可能已经进入关闭流程。
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                // Dispatcher 已经进入关闭流程。
            }
        }
    }

    private void NotifyIfInBackground(
        string title,
        string message,
        bool isError = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                new Action(() => NotifyIfInBackground(title, message, isError)));
            return;
        }

        if (!_isHiddenToTray &&
            IsVisible &&
            WindowState != WindowState.Minimized)
        {
            return;
        }

        _trayIcon?.ShowBalloon(title, message, isError);
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new HistoryWindow(_historyStore)
        {
            Owner = this
        };

        window.ShowDialog();
    }

    private void RecordHistory(
        TransferDirection direction,
        string fileName,
        long fileSize,
        TransferOutcome outcome,
        string? filePath = null,
        string? errorMessage = null)
    {
        try
        {
            _historyStore.Add(
                new TransferHistoryEntry(
                    DateTimeOffset.Now,
                    direction,
                    string.IsNullOrWhiteSpace(fileName)
                        ? "未知文件"
                        : fileName,
                    Math.Max(0, fileSize),
                    outcome,
                    _connectedPeerName,
                    filePath,
                    errorMessage));
        }
        catch (Exception exception)
        {
            Log($"记录传输历史失败：{exception.GetType().Name}");
        }
    }

    private void UpdateUi()
    {
        if (_isClosing)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(UpdateUi));
            return;
        }

        var supported = CapabilitySupported;
        var connected = _channel is not null;
        var busy = _isScanning || _isConnecting || _isHandlingRequest;

        RoleReceiverRadio.IsEnabled = supported && !connected && !busy;
        RoleSenderRadio.IsEnabled = supported && !connected && !busy;

        SameNamePolicyPanel.Visibility = Visibility.Visible;
        SameNamePolicyComboBox.IsEnabled = true;

        var waiting = _serviceStarted && _role == AppRole.Receiver && !connected;
        WaitButton.Content = waiting ? "等待中…" : "等待连接";
        WaitButton.IsEnabled = supported &&
                               _role == AppRole.Receiver &&
                               !connected &&
                               !_isHandlingRequest &&
                               !waiting;

        ScanButton.IsEnabled = supported &&
                               _role == AppRole.Sender &&
                               !connected &&
                               !_isScanning &&
                               !_isConnecting;

        PeerList.IsEnabled = supported &&
                             _role == AppRole.Sender &&
                             !connected &&
                             !_isConnecting;

        ConnectButton.IsEnabled = supported &&
                                  _role == AppRole.Sender &&
                                  !connected &&
                                  !_isScanning &&
                                  !_isConnecting &&
                                  PeerList.SelectedItem is PeerListItem;

        QuickReconnectButton.IsEnabled = supported &&
                                         _role == AppRole.Sender &&
                                         !connected &&
                                         !_isScanning &&
                                         !_isConnecting &&
                                         PeerList.SelectedItem is PeerListItem { IsKnown: true };

        var queue = _queueManager;
        var queueRunning = queue?.IsRunning == true;
        var canTransfer = supported &&
                          connected &&
                          _messageLayerReady &&
                          _dispatcher is not null &&
                          _step0Passed &&
                          !busy;

        QueuePanel.Visibility = queue is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

        ReceivedFilesPanel.Visibility =
            _fileReceiver is not null || _receivedFiles.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        SendTestFileButton.IsEnabled = canTransfer &&
                                       !_isSending &&
                                       !queueRunning;

        AddQueueFilesButton.IsEnabled = canTransfer &&
                                        !_isSending &&
                                        !queueRunning &&
                                        queue is not null;

        StartQueueButton.IsEnabled = canTransfer &&
                                     !_isSending &&
                                     !queueRunning &&
                                     queue?.CanStart == true;

        ClearQueueButton.IsEnabled = canTransfer &&
                                     !_isSending &&
                                     !queueRunning &&
                                     queue?.HasFiles == true;

        ScanButton.Content = _isScanning ? "搜索中…" : "扫描设备";
    }

    private void UpdateStatus(string status)
    {
        if (_isClosing)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            StatusText.Text = status;
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isClosing)
            {
                StatusText.Text = status;
            }
        }));
    }

    private void Log(string message)
    {
        if (_isClosing)
        {
            return;
        }

        WriteLogLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
    }

    private void LogRaw(string message)
    {
        if (_isClosing)
        {
            return;
        }

        WriteLogLine(message);
    }

    private string LogException(
        Exception exception,
        WifiDirectOperation operation,
        WifiDirectErrorContext context,
        CancellationToken userCancellationToken)
    {
        var errorCode = WifiDirectErrorMapper.Map(
            exception,
            operation,
            context,
            userCancellationToken);
        var message = WifiDirectErrorMapper.GetUserMessage(errorCode);

        Log(
            $"ERROR: code={errorCode}, " +
            $"message={message}, " +
            $"type={exception.GetType().Name}");

        return message;
    }

    private void LogErrorCode(WifiDirectErrorCode errorCode)
    {
        Log(
            $"ERROR: code={errorCode}, " +
            $"message={WifiDirectErrorMapper.GetUserMessage(errorCode)}");
    }

    private void WriteLogLine(string line)
    {
        if (_isClosing)
        {
            return;
        }

        // 先落盘：UI 线程可能正被大图片处理阻塞，文件日志不应等待它。
        AppLog.Write(line);

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => AppendLogLine(line)));
            return;
        }

        AppendLogLine(line);
    }

    private void AppendLogLine(string line)
    {
        if (_isClosing)
        {
            return;
        }

        LogTextBox.AppendText(line + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} 字节",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.0} GB"
    };

    private static string GetBuildId()
    {
        var assembly = typeof(MainWindow).Assembly;

        return assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static string GetBuildInfo()
    {
        var assembly = typeof(MainWindow).Assembly;
        var version = GetBuildId();
        var buildTime = "未知";
        var executablePath = Environment.ProcessPath;

        try
        {
            if (!string.IsNullOrEmpty(executablePath))
            {
                buildTime = File
                    .GetLastWriteTime(executablePath)
                    .ToString("yyyy-MM-dd HH:mm:ss");
            }
        }
        catch (IOException)
        {
            // 无法读取构建时间时保持“未知”。
        }
        catch (UnauthorizedAccessException)
        {
            // 无法读取构建时间时保持“未知”。
        }

        return
            $"version={version}, {(Environment.Is64BitProcess ? "x64" : "x86")}, " +
            $"build={buildTime}, clipboardMax={ClipboardMessageCodec.MaxImageBytes}, " +
            $"frameMax={ClipboardMessageCodec.MaxPayloadLength}";
    }

    private static string GetReceiveDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = string.IsNullOrWhiteSpace(profile)
            ? Path.GetTempPath()
            : profile;

        return Path.Combine(root, "Downloads", "WifiDirectTransfer");
    }

    private static string GetLocalDisplayName()
    {
        var name = Environment.MachineName;

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Windows-PC";
        }

        return name.Length > 255 ? name[..255] : name;
    }

    private static byte[] CreateTestPayload(int length)
    {
        var payload = new byte[length];

        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(((index * 31) + 7) % 256);
        }

        return payload;
    }
}

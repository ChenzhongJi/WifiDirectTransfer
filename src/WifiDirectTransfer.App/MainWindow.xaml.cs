using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
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
    private readonly ReceivedProjectCollection _receivedProjects;
    private readonly KnownDeviceStore _knownDeviceStore = new();
    private readonly Dictionary<string, KnownDevice> _knownDevices =
        new(StringComparer.Ordinal);
    private readonly List<WifiDirectPeer> _discoveredPeers = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private string _receiveDirectory;
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
    private readonly TextInbox _textInbox = new();
    private TextInboxWindow? _textInboxWindow;
    private bool _textReceiveEnabled;
    private bool _textConsentAcknowledged;
    private bool _isApplyingTextPreference;
    private bool _notificationsEnabled = true;
    private bool _isApplyingNotificationsPreference;
    private AppThemeMode _themeMode = AppThemeMode.System;
    private bool _isApplyingThemePreference;
    private bool _isApplyingExplorerContextMenuPreference;
    private readonly List<string> _pendingExternalPaths = new();
    private HwndSource? _hwndSource;
    private Point _receivedDragStart;
    private ReceivedProject? _receivedDragItem;
    private CurrentReceiveItem? _currentReceiveItem;
    private bool _isDraggingReceivedFile;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _connectionAttemptCts;
    private DispatcherTimer? _advertisementWatchdog;
    private bool _isRestartingAdvertisement;
    private bool _isRestartingDiscovery;
    private bool _isRecoveringAfterSystemEvent;
    private bool _systemRecoveryScheduled;
    private bool _pendingFullSystemReset;
    private string _systemRecoveryReason = string.Empty;
    private bool _sessionNotificationRegistered;
    private DateTimeOffset _suppressNetworkChangeUntil = DateTimeOffset.MinValue;

    private static readonly TimeSpan AdvertisementRefreshInterval =
        TimeSpan.FromMinutes(2);

    private static readonly TimeSpan NetworkChangeSuppression =
        TimeSpan.FromSeconds(5);

    private const string ReceivedFileDragFormat =
        "WifiDirectTransfer.ReceivedFileDrag";

    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;

    private const int WmWtssessionChange = 0x02B1;
    private const int WtsSessionLock = 0x7;
    private const int WtsSessionUnlock = 0x8;
    private const int NotifyForThisSession = 0;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(
        IntPtr hWnd,
        int dwFlags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

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
    private bool _isDisconnecting;
    private bool _isSending;
    private bool _isHandlingRequest;
    private string? _connectedPeerName;
    private WifiDirectPeer? _connectedPeer;
    private bool _isAutoReconnecting;
    private bool _suppressAutoReconnect;

    private sealed record AutoReconnectPlan(
        WifiDirectPeer Peer,
        IReadOnlyList<TransferSource> Files,
        IReadOnlyList<string> EmptyDirectories);
    private string? _peerBuild;
    private bool _step0Passed;
    private bool _messageLayerReady;
    private volatile bool _isClosing;
    private bool _allowClose;
    private bool _isHiddenToTray;
    private TrayIconService? _trayIcon;
    private int _uiLogLineCount;
    private bool _isLogCollapsed;

    private const int MaxUiLogLines = 2000;
    private const int UiLogTrimBatch = 500;
    private const double ExpandedLogHeight = 160;

    public MainWindow()
    {
        _receiveDirectory = GetDefaultReceiveDirectory();
        _receivedProjects = new ReceivedProjectCollection(
            () => _receiveDirectory);

        InitializeComponent();

        ProtocolVersionText.Text =
            $"双向传输 · 协议 v{MessageProtocol.ProtocolVersion}";

        SendPanel.Visibility = Visibility.Visible;
        ReceivePanel.Visibility = Visibility.Collapsed;

        _historyStore = new TransferHistoryStore(log: Log);

        _clipboardSyncService = new ClipboardSyncService(
            Dispatcher,
            Log,
            UpdateStatus,
            _lifetimeCts.Token);

        PeerList.ItemsSource = _peerItems;
        ReceivedFilesList.ItemsSource = _receivedProjects.Projects;
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

            _sessionNotificationRegistered =
                WTSRegisterSessionNotification(
                    source.Handle,
                    NotifyForThisSession);

            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
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
        else if (message == WmPowerBroadcast)
        {
            var powerEvent = wParam.ToInt32();

            if (powerEvent == PbtApmSuspend)
            {
                HandleSystemSuspend();
            }
            else if (powerEvent == PbtApmResumeSuspend ||
                     powerEvent == PbtApmResumeAutomatic)
            {
                ScheduleSystemRecovery("休眠恢复", fullReset: true);
            }
        }
        else if (message == WmWtssessionChange)
        {
            var sessionEvent = wParam.ToInt32();

            if (sessionEvent == WtsSessionLock)
            {
                HandleSessionLock();
            }
            else if (sessionEvent == WtsSessionUnlock)
            {
                ScheduleSystemRecovery("解锁", fullReset: false);
            }
        }

        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _advertisementWatchdog?.Stop();
        _advertisementWatchdog = null;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;

        if (_hwndSource is not null)
        {
            if (_sessionNotificationRegistered)
            {
                WTSUnRegisterSessionNotification(_hwndSource.Handle);
                _sessionNotificationRegistered = false;
            }

            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        Application.Current.SessionEnding -= OnSessionEnding;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _clipboardSyncService.Dispose();
        base.OnClosed(e);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        if (_initialized && !_isClosing)
        {
            RefreshReceivedFilesAvailability();
        }
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

        StatusText.Text = "未连接";
        Log("M0 界面已启动。");
        Log($"日志文件：{AppLog.FilePath}");
        Log($"本端构建：{GetBuildInfo()}");
        InitializeTrayIcon();

        LoadKnownDevices();
        RebuildPeerList(Array.Empty<WifiDirectPeer>());
        Log($"历史设备：{_knownDevices.Count} 台。");

        ApplyReceiverPreferences();
        UpdateReceiveDirectoryText();
        Log($"接收目录：{_receiveDirectory}");
        Log($"同名文件策略：{GetSameNamePolicyText(_sameNamePolicy)}");
        Log(_clipboardSyncEnabled ? "剪贴板同步：已启用。" : "剪贴板同步：已关闭。");

        CheckCapability();

        _role = AppRole.Receiver;
        RoleReceiverRadio.IsChecked = true;

        UpdateUi();
        UpdateTextInboxButton();

        var launchPaths = Program.TakePendingPaths();

        if (launchPaths.Count > 0)
        {
            ActivateFromExternalLaunch(launchPaths);
        }

        if (_pendingExternalPaths.Count > 0)
        {
            FlushPendingExternalPaths();
        }

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
        _receiveDirectory = ResolveReceiveDirectory(
            preferences.ReceiveDirectory);
        _clipboardConsentAcknowledged =
            preferences.ClipboardConsentAcknowledged;
        _clipboardSyncEnabled =
            preferences.ClipboardSyncEnabled &&
            _clipboardConsentAcknowledged;
        _textConsentAcknowledged = preferences.TextConsentAcknowledged;
        _textReceiveEnabled =
            preferences.TextReceiveEnabled &&
            _textConsentAcknowledged;
        _notificationsEnabled = preferences.NotificationsEnabled;
        _themeMode = preferences.ThemeMode;

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

        _isApplyingTextPreference = true;

        try
        {
            TextReceiveCheckBox.IsChecked = _textReceiveEnabled;
        }
        finally
        {
            _isApplyingTextPreference = false;
        }

        _isApplyingNotificationsPreference = true;

        try
        {
            NotificationsCheckBox.IsChecked = _notificationsEnabled;
        }
        finally
        {
            _isApplyingNotificationsPreference = false;
        }

        _isApplyingThemePreference = true;

        try
        {
            ThemeModeComboBox.SelectedIndex = _themeMode switch
            {
                AppThemeMode.Dark => 2,
                AppThemeMode.Light => 1,
                _ => 0
            };
        }
        finally
        {
            _isApplyingThemePreference = false;
        }

        ThemeManager.Apply(_themeMode);
        _clipboardSyncService.SetEnabled(_clipboardSyncEnabled);

        _isApplyingExplorerContextMenuPreference = true;

        try
        {
            var explorerRegistered = ExplorerContextMenu.IsRegistered();
            ExplorerContextMenuCheckBox.IsChecked = explorerRegistered;

            if (explorerRegistered)
            {
                var executablePath = GetExecutablePath();

                if (!string.IsNullOrWhiteSpace(executablePath) &&
                    !ExplorerContextMenu.IsRegisteredFor(executablePath) &&
                    !ExplorerContextMenu.Register(executablePath))
                {
                    Log("更新资源管理器右键菜单失败。");
                }
            }
        }
        finally
        {
            _isApplyingExplorerContextMenuPreference = false;
        }
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
                ClipboardConsentAcknowledged = _clipboardConsentAcknowledged,
                TextReceiveEnabled = _textReceiveEnabled,
                TextConsentAcknowledged = _textConsentAcknowledged,
                NotificationsEnabled = _notificationsEnabled,
                ThemeMode = _themeMode,
                ReceiveDirectory = _receiveDirectory
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

    private void TextReceiveCheckBox_Checked(
        object sender,
        RoutedEventArgs e)
    {
        if (!_initialized || _isApplyingTextPreference || _isClosing)
        {
            return;
        }

        if (!_textConsentAcknowledged)
        {
            var dialog = new TextReceiveConfirmationWindow
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
            {
                _isApplyingTextPreference = true;

                try
                {
                    TextReceiveCheckBox.IsChecked = false;
                }
                finally
                {
                    _isApplyingTextPreference = false;
                }

                return;
            }

            _textConsentAcknowledged = true;
        }

        _textReceiveEnabled = true;

        var saved = SaveReceiverPreferences();
        Log("接收远程文本已启用。" + (saved ? string.Empty : "（保存失败）"));
    }

    private void TextReceiveCheckBox_Unchecked(
        object sender,
        RoutedEventArgs e)
    {
        if (!_initialized || _isApplyingTextPreference || _isClosing)
        {
            return;
        }

        _textReceiveEnabled = false;

        var saved = SaveReceiverPreferences();
        Log("接收远程文本已关闭。" + (saved ? string.Empty : "（保存失败）"));
    }

    private void ThemeModeComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_initialized || _isApplyingThemePreference || _isClosing)
        {
            return;
        }

        if (ThemeModeComboBox.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string tag ||
            !Enum.TryParse<AppThemeMode>(tag, out var mode) ||
            !Enum.IsDefined(mode))
        {
            return;
        }

        _themeMode = mode;
        ThemeManager.Apply(mode);

        var saved = SaveReceiverPreferences();
        var text = mode switch
        {
            AppThemeMode.Dark => "深色",
            AppThemeMode.Light => "浅色",
            _ => "跟随系统"
        };

        Log($"主题：{text}" + (saved ? string.Empty : "（保存失败）"));
    }

    private void NotificationsCheckBox_Checked(
        object sender,
        RoutedEventArgs e)
    {
        if (!_initialized || _isApplyingNotificationsPreference || _isClosing)
        {
            return;
        }

        _notificationsEnabled = true;

        var saved = SaveReceiverPreferences();
        Log("系统通知已启用。" + (saved ? string.Empty : "（保存失败）"));
    }

    private void NotificationsCheckBox_Unchecked(
        object sender,
        RoutedEventArgs e)
    {
        if (!_initialized || _isApplyingNotificationsPreference || _isClosing)
        {
            return;
        }

        _notificationsEnabled = false;

        var saved = SaveReceiverPreferences();
        Log("系统通知已关闭。" + (saved ? string.Empty : "（保存失败）"));
    }

    private void ExplorerContextMenuCheckBox_Checked(
        object sender,
        RoutedEventArgs e)
    {
        if (!_initialized ||
            _isApplyingExplorerContextMenuPreference ||
            _isClosing)
        {
            return;
        }

        var executablePath = GetExecutablePath();

        if (string.IsNullOrWhiteSpace(executablePath) ||
            !ExplorerContextMenu.Register(executablePath))
        {
            _isApplyingExplorerContextMenuPreference = true;

            try
            {
                ExplorerContextMenuCheckBox.IsChecked = false;
            }
            finally
            {
                _isApplyingExplorerContextMenuPreference = false;
            }

            Log("注册资源管理器右键菜单失败。");
            UpdateStatus("注册资源管理器右键菜单失败");
            return;
        }

        Log("已注册资源管理器右键菜单。");
        UpdateStatus("已启用资源管理器右键发送");
    }

    private void ExplorerContextMenuCheckBox_Unchecked(
        object sender,
        RoutedEventArgs e)
    {
        if (!_initialized ||
            _isApplyingExplorerContextMenuPreference ||
            _isClosing)
        {
            return;
        }

        if (!ExplorerContextMenu.Unregister())
        {
            _isApplyingExplorerContextMenuPreference = true;

            try
            {
                ExplorerContextMenuCheckBox.IsChecked = true;
            }
            finally
            {
                _isApplyingExplorerContextMenuPreference = false;
            }

            Log("移除资源管理器右键菜单失败。");
            UpdateStatus("移除资源管理器右键菜单失败");
            return;
        }

        Log("已移除资源管理器右键菜单。");
        UpdateStatus("已关闭资源管理器右键发送");
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
        var usedDiscoveredIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var known in _knownDevices.Values
                     .OrderByDescending(device => device.LastConnectedAt))
        {
            var discovered = discoveredSnapshot.FirstOrDefault(
                peer => !usedDiscoveredIds.Contains(peer.Id) &&
                        string.Equals(
                            peer.Id,
                            known.Id,
                            StringComparison.Ordinal));

            if (discovered is null)
            {
                var available = discoveredSnapshot
                    .Where(peer => !usedDiscoveredIds.Contains(peer.Id))
                    .ToArray();

                discovered = PeerIdentityMatcher.FindUniqueByName(
                    available,
                    known.DisplayName);
            }

            var isDiscovered = discovered is not null;
            var peer = discovered ??
                       new WifiDirectPeer(known.Id, known.DisplayName);

            if (isDiscovered)
            {
                usedDiscoveredIds.Add(peer.Id);
            }

            if (addedIds.Add(peer.Id))
            {
                items.Add(new PeerListItem(peer, known, isDiscovered));
            }
        }

        foreach (var peer in discoveredSnapshot)
        {
            if (usedDiscoveredIds.Contains(peer.Id) ||
                !addedIds.Add(peer.Id))
            {
                continue;
            }

            items.Add(new PeerListItem(peer, null, isDiscovered: true));
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
            var firstDiscovered = _peerItems.FirstOrDefault(
                item => item.IsDiscovered);

            if (firstDiscovered is not null)
            {
                PeerList.SelectedItem = firstDiscovered;
            }
        }

        UpdateUi();
    }

    private void RecordKnownDevice(WifiDirectPeer peer)
    {
        if (string.IsNullOrWhiteSpace(peer.Id))
        {
            return;
        }

        // 某些驱动每次扫描都会生成新的 PeerInformation.Id。连接成功后，
        // 清掉同名但旧 ID 的历史记录，避免下次启动继续选中过期设备。
        var staleIds = _knownDevices
            .Where(pair =>
                !string.Equals(pair.Key, peer.Id, StringComparison.Ordinal) &&
                PeerIdentityMatcher.IsSameDisplayName(
                    pair.Value.DisplayName,
                    peer.DisplayName))
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var staleId in staleIds)
        {
            _knownDevices.Remove(staleId);
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
        SuppressNetworkChangeRecovery();

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
        if (_role != AppRole.Receiver ||
            _channel is not null ||
            _isHandlingRequest ||
            _isRestartingAdvertisement)
        {
            return;
        }

        if (_serviceStarted)
        {
            await RestartReceiverAdvertisementAsync("用户重新等待");
            return;
        }

        if (!await EnsureServiceStartedAsync())
        {
            return;
        }

        Log("等待端已就绪，等待连接发起端连接。");
        UpdateStatus("等待连接…如长时间无法被发现，点击“重新等待”。");
        StartAdvertisementWatchdog();
        UpdateUi();
    }

    private async Task RestartReceiverAdvertisementAsync(string reason)
    {
        if (_isRestartingAdvertisement ||
            _isClosing ||
            _channel is not null ||
            _isHandlingRequest)
        {
            return;
        }

        _isRestartingAdvertisement = true;
        UpdateUi();
        UpdateStatus("正在重新启动等待连接…");
        Log($"重新启动接收端等待：{reason}。");

        try
        {
            if (!await RestartPeerFinderAsync(reason, _lifetimeCts.Token))
            {
                UpdateStatus(
                    "重新启动等待失败，请尝试关闭再打开 Wi-Fi 或重启应用。");
                return;
            }

            UpdateStatus("等待连接已重新启动，请让对方重新扫描。");
            StartAdvertisementWatchdog();
        }
        catch (Exception exception)
        {
            var message = LogException(
                exception,
                WifiDirectOperation.Connection,
                WifiDirectErrorContext.WinRt,
                _lifetimeCts.Token);
            UpdateStatus(message);
        }
        finally
        {
            _isRestartingAdvertisement = false;
            UpdateUi();
        }
    }

    private async Task RecoverDiscoveryAfterConnectionEndAsync(string reason)
    {
        if (_isClosing ||
            _channel is not null ||
            _isConnecting ||
            _isHandlingRequest ||
            _isRecoveringAfterSystemEvent)
        {
            return;
        }

        if (_role == AppRole.Receiver)
        {
            if (_serviceStarted)
            {
                await RestartReceiverAdvertisementAsync(reason);
            }

            return;
        }

        if (!_serviceStarted)
        {
            return;
        }

        _isRestartingDiscovery = true;
        UpdateUi();
        Log($"重新启动发送端发现：{reason}。");

        try
        {
            if (!await RestartPeerFinderAsync(reason, _lifetimeCts.Token))
            {
                UpdateStatus("重新启动发现失败，请重新扫描或重启应用。");
                return;
            }

            UpdateStatus("已断开连接，请重新扫描设备。");
        }
        catch (Exception exception)
        {
            var message = LogException(
                exception,
                WifiDirectOperation.Connection,
                WifiDirectErrorContext.WinRt,
                _lifetimeCts.Token);
            UpdateStatus(message);
        }
        finally
        {
            _isRestartingDiscovery = false;
            UpdateUi();
        }
    }

    private void UpdateAdvertisementWatchdog(bool waiting)
    {
        if (waiting)
        {
            StartAdvertisementWatchdog();
        }
        else
        {
            StopAdvertisementWatchdog();
        }
    }

    private void StartAdvertisementWatchdog()
    {
        if (_isClosing)
        {
            return;
        }

        if (_advertisementWatchdog is null)
        {
            _advertisementWatchdog = new DispatcherTimer
            {
                Interval = AdvertisementRefreshInterval
            };
            _advertisementWatchdog.Tick += AdvertisementWatchdog_Tick;
        }

        if (!_advertisementWatchdog.IsEnabled)
        {
            _advertisementWatchdog.Start();
        }
    }

    private void StopAdvertisementWatchdog()
    {
        _advertisementWatchdog?.Stop();
    }

    private async void AdvertisementWatchdog_Tick(
        object? sender,
        EventArgs e)
    {
        if (_isClosing ||
            _isRestartingAdvertisement ||
            _isRecoveringAfterSystemEvent)
        {
            return;
        }

        if (_role != AppRole.Receiver ||
            _channel is not null ||
            !_serviceStarted ||
            _isHandlingRequest ||
            _currentReceiveItem is not null)
        {
            StopAdvertisementWatchdog();
            return;
        }

        await RestartReceiverAdvertisementAsync("定期刷新等待广播");
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        await ScanForPeersAsync();
    }

    private async Task<bool> ScanForPeersAsync()
    {
        if (_role != AppRole.Sender ||
            _channel is not null ||
            _isScanning ||
            _isRestartingDiscovery ||
            _isAutoReconnecting ||
            _isClosing)
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
        Log("FindAllPeersAsync 开始：搜索窗口 15 秒，自动重试。");

        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _scanCts = scanCts;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        var attempt = 0;
        var restarted = false;
        IReadOnlyList<WifiDirectPeer> peers = Array.Empty<WifiDirectPeer>();

        try
        {
            while (!_isClosing && DateTimeOffset.UtcNow < deadline)
            {
                attempt++;
                var remaining = deadline - DateTimeOffset.UtcNow;

                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                try
                {
                    peers = await _service.FindPeersAsync(
                        remaining,
                        scanCts.Token);
                }
                catch (DiscoveryTimeoutException)
                {
                    peers = Array.Empty<WifiDirectPeer>();
                }
                catch (OperationCanceledException) when (scanCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception) when (attempt <= 2 && !_lifetimeCts.IsCancellationRequested)
                {
                    Log($"FindAllPeersAsync 第 {attempt} 次异常，准备重试。");
                    peers = Array.Empty<WifiDirectPeer>();
                }

                Log($"FindAllPeersAsync 第 {attempt} 次返回 {peers.Count} 台设备。");

                foreach (var peer in peers)
                {
                    Log($"  peer: DisplayName={peer.DisplayName}, Id={peer.Id}");
                }

                if (peers.Count > 0)
                {
                    break;
                }

                if (!restarted &&
                    attempt >= 3 &&
                    DateTimeOffset.UtcNow < deadline)
                {
                    restarted = true;
                    UpdateStatus("未发现设备，正在重新启动搜索…");
                    Log("连续 3 次未发现设备，重启 PeerFinder。");

                    if (!await RestartPeerFinderAsync(
                            "连续 3 次未发现设备",
                            scanCts.Token))
                    {
                        break;
                    }

                    UpdateStatus("正在搜索设备…");
                    continue;
                }

                if (attempt >= 3)
                {
                    UpdateStatus("仍在搜索，请确认对端已点击“等待连接”…");
                }

                try
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(700),
                        scanCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (_isClosing || scanCts.IsCancellationRequested)
            {
                return false;
            }

            RebuildPeerList(peers);

            if (peers.Count == 0)
            {
                Log($"搜索结束：尝试 {attempt} 次，未发现设备。");
                UpdateStatus(
                    "未发现设备。请确认对端已选择“等待连接”并点击“等待连接”，" +
                    "两台电脑保持前台后重试。");
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
            if (ReferenceEquals(_scanCts, scanCts))
            {
                _scanCts = null;
            }

            _isScanning = false;
            UpdateUi();
        }
    }

    private async Task<bool> RestartPeerFinderAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        Log($"重新启动 PeerFinder：{reason}。");
        SuppressNetworkChangeRecovery();

        try
        {
            await _service.StopAsync(cancellationToken);
            _serviceStarted = false;
            Log("PeerFinder 已停止。");
        }
        catch (Exception exception)
        {
            Log($"停止 PeerFinder 失败：{exception.GetType().Name}");
            _serviceStarted = false;
        }

        if (_isClosing || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (!await EnsureServiceStartedAsync())
        {
            return false;
        }

        Log("PeerFinder 已重新启动。");
        return true;
    }

    private void HandleSystemSuspend()
    {
        if (_isClosing)
        {
            return;
        }

        Log("系统即将休眠，取消当前扫描和连接尝试。");
        _scanCts?.Cancel();
        _connectionAttemptCts?.Cancel();
    }

    private void HandleSessionLock()
    {
        if (_isClosing)
        {
            return;
        }

        Log("系统已锁屏，取消当前扫描和连接尝试。");
        _scanCts?.Cancel();
        _connectionAttemptCts?.Cancel();
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (!_initialized || _isClosing)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                new Action(() => OnNetworkAddressChanged(sender, e)));
            return;
        }

        if (DateTimeOffset.UtcNow < _suppressNetworkChangeUntil)
        {
            Log("忽略由 PeerFinder 启停触发的网络状态变化。");
            return;
        }

        if (_isConnecting ||
            _isScanning ||
            _isHandlingRequest ||
            _isRestartingDiscovery ||
            _isAutoReconnecting ||
            _channel is not null ||
            _queueManager is not null ||
            _currentReceiveItem is not null)
        {
            Log("连接、扫描或传输进行中，忽略网络状态变化。");
            return;
        }

        ScheduleSystemRecovery("网络状态变化", fullReset: false);
    }

    private void SuppressNetworkChangeRecovery()
    {
        _suppressNetworkChangeUntil = DateTimeOffset.UtcNow + NetworkChangeSuppression;
    }

    private void ScheduleSystemRecovery(string reason, bool fullReset)
    {
        if (_isClosing)
        {
            return;
        }

        _pendingFullSystemReset |= fullReset;
        _systemRecoveryReason = reason;

        if (_systemRecoveryScheduled)
        {
            return;
        }

        _systemRecoveryScheduled = true;

        _ = Dispatcher.BeginInvoke(
            new Action(async () =>
            {
                _systemRecoveryScheduled = false;
                var doFullReset = _pendingFullSystemReset;
                _pendingFullSystemReset = false;

                await RecoverAfterSystemEventAsync(
                    _systemRecoveryReason,
                    doFullReset);
            }),
            DispatcherPriority.Background);
    }

    private async Task RecoverAfterSystemEventAsync(
        string reason,
        bool fullReset)
    {
        if (_isRecoveringAfterSystemEvent || _isClosing)
        {
            return;
        }

        if (!fullReset &&
            (_isConnecting ||
             _isScanning ||
             _isHandlingRequest ||
             _isRestartingDiscovery ||
             _isAutoReconnecting ||
             _channel is not null))
        {
            Log($"连接或扫描进行中，跳过恢复：{reason}。");
            return;
        }

        _isRecoveringAfterSystemEvent = true;
        Log($"系统恢复处理开始：{reason}，fullReset={fullReset}。");

        try
        {
            if (fullReset)
            {
                _scanCts?.Cancel();
                _connectionAttemptCts?.Cancel();
                await WaitForActiveOperationsAsync(TimeSpan.FromSeconds(3));
            }

            if (_isClosing)
            {
                return;
            }

            if (fullReset)
            {
                if (_channel is not null ||
                    _queueManager is not null ||
                    _currentReceiveItem is not null ||
                    _fileReceiver is not null)
                {
                    await ResetConnectionAsync();
                }

                var wasStarted = _serviceStarted;
                var wasReceiver = _role == AppRole.Receiver;

                if (wasStarted)
                {
                    StopAdvertisementWatchdog();
                    SuppressNetworkChangeRecovery();

                    try
                    {
                        await _service.StopAsync(_lifetimeCts.Token);
                    }
                    catch (Exception exception)
                    {
                        Log($"恢复时停止 PeerFinder 失败：{exception.GetType().Name}");
                    }

                    _serviceStarted = false;
                }

                if (wasStarted && !_isClosing)
                {
                    if (!await EnsureServiceStartedAsync())
                    {
                        UpdateStatus(
                            "已从休眠恢复，但重新启动发现失败，" +
                            "请重新等待或扫描。");
                        return;
                    }

                    if (wasReceiver)
                    {
                        UpdateStatus("已从休眠恢复，正在等待连接…");
                        StartAdvertisementWatchdog();
                    }
                    else
                    {
                        UpdateStatus("已从休眠恢复，请重新扫描设备。");
                    }
                }
                else if (!_isClosing)
                {
                    UpdateStatus("已从休眠恢复");
                }
            }
            else
            {
                if (_channel is not null)
                {
                    UpdateUi();
                    return;
                }

                if (_serviceStarted)
                {
                    if (!await RestartPeerFinderAsync(
                            reason,
                            _lifetimeCts.Token))
                    {
                        UpdateStatus(
                            "恢复发现失败，请点击“重新等待”或重启应用。");
                        return;
                    }

                    if (_role == AppRole.Receiver)
                    {
                        UpdateStatus("已恢复等待连接，请让对方重新扫描。");
                        StartAdvertisementWatchdog();
                    }
                    else
                    {
                        UpdateStatus("已恢复，请重新扫描设备。");
                    }
                }
                else
                {
                    UpdateStatus("已恢复");
                }
            }

            UpdateUi();
        }
        catch (Exception exception)
        {
            var message = LogException(
                exception,
                WifiDirectOperation.Connection,
                WifiDirectErrorContext.WinRt,
                _lifetimeCts.Token);
            UpdateStatus(message);
        }
        finally
        {
            _isRecoveringAfterSystemEvent = false;
        }
    }

    private async Task WaitForActiveOperationsAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (!_isClosing &&
               (_isScanning || _isConnecting) &&
               DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    private void PeerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateUi();

        if (PeerList.SelectedItem is PeerListItem { IsKnownOnly: true } item)
        {
            UpdateStatus(
                $"“{item.DisplayName}”本次扫描未发现，" +
                "请确认对端正在等待连接后重新扫描。");
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_role != AppRole.Sender ||
            _channel is not null ||
            _isConnecting ||
            _isRestartingDiscovery ||
            _isAutoReconnecting ||
            PeerList.SelectedItem is not PeerListItem { IsDiscovered: true } selectedItem)
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
        _connectionAttemptCts = connectCts;

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
            _connectedPeer = peer;
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
            if (ReferenceEquals(_connectionAttemptCts, connectCts))
            {
                _connectionAttemptCts = null;
            }

            _isConnecting = false;
            UpdateUi();
        }
    }

    private async void DisconnectButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_isDisconnecting ||
            _isConnecting ||
            _isHandlingRequest ||
            _channel is null ||
            _isClosing)
        {
            return;
        }

        var hasActiveWork =
            _isSending ||
            _isHandlingRequest ||
            _currentReceiveItem is not null ||
            _queueManager is { HasWaitingFiles: true };

        if (hasActiveWork)
        {
            var result = MessageBox.Show(
                this,
                "当前连接或传输尚未结束。断开连接会中断正在进行的传输；" +
                "接收中的文件会保留断点，发送队列中尚未开始的文件会被移除。" +
                "\n\n是否继续？",
                "断开连接",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _isDisconnecting = true;
        _suppressAutoReconnect = true;
        DisconnectButton.IsEnabled = false;
        UpdateStatus("正在断开连接…");
        Log("用户请求断开连接。");

        try
        {
            await ResetConnectionAsync();
            await RecoverDiscoveryAfterConnectionEndAsync("用户断开连接");
        }
        catch (Exception exception)
        {
            var message = LogException(
                exception,
                WifiDirectOperation.Connection,
                WifiDirectErrorContext.WinRt,
                CancellationToken.None);
            UpdateStatus(message);
        }
        finally
        {
            _isDisconnecting = false;
            _suppressAutoReconnect = false;

            if (!_isClosing)
            {
                UpdateUi();
            }
        }
    }

    private async void QuickReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_role != AppRole.Sender ||
            _channel is not null ||
            _isScanning ||
            _isConnecting ||
            _isRestartingDiscovery ||
            _isAutoReconnecting ||
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
        _connectionAttemptCts = connectCts;

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
            if (ReferenceEquals(_connectionAttemptCts, connectCts))
            {
                _connectionAttemptCts = null;
            }

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

        var connectionEstablished = false;

        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            ShowFromTray();

            if (_notificationsEnabled)
            {
                _trayIcon?.ShowBalloon(
                    "连接请求",
                    $"来自 {request.Peer.DisplayName} 的 Wi-Fi Direct 连接请求。");
            }
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

            connectionEstablished = true;
            RecordKnownDevice(request.Peer);
            _connectedPeer = request.Peer;
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

            if (!connectionEstablished &&
                !_isClosing &&
                _serviceStarted &&
                _channel is null)
            {
                await RestartReceiverAdvertisementAsync("连接请求处理结束");
            }
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

        _peerBuild = peerHello.Build;

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
            progress: new ThrottledProgress<FileTransferProgress>(
                value => Dispatcher.BeginInvoke(
                    new Action(() => UpdateReceiveProgress(value))),
                TimeSpan.FromMilliseconds(150),
                value => value.TotalBytes > 0 &&
                         value.BytesTransferred >= value.TotalBytes),
            sameNamePolicy: _sameNamePolicy,
            log: Log);

        receiver.FileStarted += OnFileStarted;
        receiver.FileReceived += OnFileReceived;
        receiver.FileFailed += OnFileFailed;
        receiver.FileSkipped += OnFileSkipped;
        receiver.DirectoriesReceived += OnDirectoriesReceived;
        _fileReceiver = receiver;

        var queue = new TransferQueueManager(
            dispatcher,
            PeerBuildCompatibility.GetMaxFileSize(_peerBuild),
            errorMessageFactory: CreateQueueErrorMessage,
            log: Log,
            supportsDirectoryManifest:
                PeerBuildCompatibility.SupportsDirectoryManifest(_peerBuild));

        queue.FileStarted += OnQueueFileStarted;
        queue.FileCompleted += OnQueueFileCompleted;
        queue.FileFailed += OnQueueFileFailed;
        queue.FileSkipped += OnQueueFileSkipped;
        queue.QueueCompleted += OnQueueCompleted;
        _queueManager = queue;
        SendPanel.DataContext = queue;

        dispatcher.MessageReceived += OnDispatcherMessageAsync;
        dispatcher.Disconnected += OnDispatcherDisconnected;

        _messageLayerReady = true;
        UpdateUi();

        _ = Dispatcher.BeginInvoke(
            new Action(FlushPendingExternalPaths),
            DispatcherPriority.Background);

        Log(
            $"双向传输层已就绪：role={_role}，" +
            $"maxPayload={dispatcher.MaxPayloadLength}，" +
            $"peerMaxPayload={peerHello.MaxPayloadLength}，" +
            $"peerMaxFileSize={FormatBytes(PeerBuildCompatibility.GetMaxFileSize(_peerBuild))}，" +
            $"directoryManifest={PeerBuildCompatibility.SupportsDirectoryManifest(_peerBuild)}，" +
            $"clipboardSync={_clipboardSyncEnabled}。");

        return true;
    }

    private void OnFileStarted(object? sender, FileStartedEventArgs args)
    {
        if (_isClosing || !ReferenceEquals(_fileReceiver, sender))
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                new Action(() => OnFileStarted(sender, args)));
            return;
        }

        var info = args.StartInfo;

        Log(info.IsResume
            ? $"接收：{info.FileName} 从断点继续，偏移 {info.StartOffset} 字节"
            : $"接收：{info.FileName}，{info.TotalBytes} 字节");

        UpdateStatus(info.IsResume
            ? $"正在接收：{info.FileName}（从 {FormatBytes(info.StartOffset)} 续传）…"
            : $"正在接收：{info.FileName}（{FormatBytes(info.TotalBytes)}）…");

        _currentReceiveItem = new CurrentReceiveItem(info);
        CurrentReceivePanel.DataContext = _currentReceiveItem;
        CurrentReceivePanel.Visibility = Visibility.Visible;
        _receivedProjects.BeginFile(info, DateTimeOffset.Now);
        UpdateReceivedFilesEmptyState();
        UpdateTransferTabHeaders();
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

        _receivedProjects.CompleteFile(result, DateTimeOffset.Now);
        UpdateReceivedFilesEmptyState();
        ClearCurrentReceive();

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
            _receivedProjects.FailActive(DateTimeOffset.Now);
            UpdateReceivedFilesEmptyState();
            ClearCurrentReceive();
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
        _receivedProjects.FailActive(DateTimeOffset.Now);
        UpdateReceivedFilesEmptyState();
        ClearCurrentReceive();
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
        _receivedProjects.SkipActive(DateTimeOffset.Now);
        UpdateReceivedFilesEmptyState();
        ClearCurrentReceive();
    }

    private void OnDirectoriesReceived(
        object? sender,
        DirectoryManifestEventArgs args)
    {
        if (_isClosing || !ReferenceEquals(_fileReceiver, sender))
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                new Action(() => OnDirectoriesReceived(sender, args)));
            return;
        }

        Log(
            $"接收空文件夹：created={args.CreatedCount}, " +
            $"failed={args.FailedCount}。");

        _receivedProjects.AddEmptyDirectories(
            args.CreatedDirectories,
            DateTimeOffset.Now);
        UpdateReceivedFilesEmptyState();

        UpdateStatus(args.FailedCount > 0
            ? $"已创建 {args.CreatedCount} 个空文件夹，" +
              $"{args.FailedCount} 个失败"
            : $"已创建 {args.CreatedCount} 个空文件夹");
    }

    private void ReceivedFilesList_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _receivedDragStart = e.GetPosition(ReceivedFilesList);
        _receivedDragItem = GetReceivedProject(e.OriginalSource as DependencyObject);
    }

    private void ReceivedFilesList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        UpdateReceivedFileActions();
    }

    private void ReceivedFilesList_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if (ReceivedFilesList.SelectedItem is not ReceivedProject item)
        {
            return;
        }

        e.Handled = true;
        OpenReceivedProject(item);
    }

    private void OpenReceivedFolderButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (ReceivedFilesList.SelectedItem is not ReceivedProject item)
        {
            return;
        }

        OpenReceivedProject(item);
    }

    private void UpdateReceivedFilesEmptyState()
    {
        ReceivedFilesEmptyPanel.Visibility =
            _receivedProjects.Projects.Count > 0
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private void UpdateReceivedFileActions()
    {
        var selected = ReceivedFilesList.SelectedItems
            .OfType<ReceivedProject>()
            .ToArray();

        foreach (var item in selected)
        {
            item.RefreshAvailability();
        }

        OpenReceivedFolderButton.IsEnabled =
            selected.Length > 0 &&
            selected.All(item => !item.IsMissing);
    }

    private void RefreshReceivedFilesAvailability()
    {
        if (_isClosing || _receivedProjects.Projects.Count == 0)
        {
            UpdateReceivedFileActions();
            return;
        }

        foreach (var item in _receivedProjects.Projects)
        {
            item.RefreshAvailability();
        }

        UpdateReceivedFileActions();
    }

    private void OpenReceivedProject(ReceivedProject project)
    {
        if (!project.RefreshAvailability())
        {
            ShowMissingReceivedProjects(new[] { project });
            UpdateReceivedFileActions();
            return;
        }

        try
        {
            if (project.IsFolder)
            {
                Process.Start(
                    new ProcessStartInfo(
                        "explorer.exe",
                        $"\"{project.FolderPath}\"")
                    {
                        UseShellExecute = true
                    });
                return;
            }

            var filePath = project.LatestFilePath;

            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            Process.Start(
                new ProcessStartInfo(
                    "explorer.exe",
                    $"/select,\"{filePath}\"")
                {
                    UseShellExecute = true
                });
        }
        catch (Exception exception)
        {
            Log($"打开接收项目失败：{exception.GetType().Name}");
            MessageBox.Show(
                this,
                $"无法打开文件夹：{exception.Message}",
                "接收项目",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ShowMissingReceivedProjects(
        IReadOnlyList<ReceivedProject> projects)
    {
        if (projects.Count == 0)
        {
            return;
        }

        var names = string.Join(
            Environment.NewLine,
            projects.Take(8).Select(project => project.DisplayName));

        if (projects.Count > 8)
        {
            names += $"{Environment.NewLine}… 等 {projects.Count} 个项目";
        }

        Log(
            "接收项目已不存在：" +
            string.Join(
                ", ",
                projects.Select(project =>
                    project.FolderPath ?? project.LatestFilePath)));

        UpdateStatus("接收项目已不存在，可能已被移动或删除");
        MessageBox.Show(
            this,
            "以下接收项目已不存在，可能已被移动或删除：" +
            Environment.NewLine +
            Environment.NewLine +
            names,
            "接收项目",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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
            .OfType<ReceivedProject>()
            .ToList();

        if (!items.Contains(dragItem))
        {
            items = new List<ReceivedProject> { dragItem };
        }

        var missingItems = items
            .Where(item => !item.RefreshAvailability())
            .ToArray();

        if (missingItems.Length > 0)
        {
            ShowMissingReceivedProjects(missingItems);
            UpdateReceivedFileActions();
            return;
        }

        var paths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var path = item.IsFolder
                ? item.FolderPath
                : item.LatestFilePath;

            if (string.IsNullOrWhiteSpace(path) || !seenPaths.Add(path))
            {
                continue;
            }

            if ((item.IsFolder && Directory.Exists(path)) ||
                (!item.IsFolder && File.Exists(path)))
            {
                paths.Add(path);
            }
        }

        if (paths.Count == 0)
        {
            Log("接收项目已不存在，无法拖出。");
            UpdateStatus("接收项目已不存在");
            return;
        }

        // W3 折中：未实现 VirtualFileDataObject 的 CFSTR_FILEDESCRIPTOR /
        // CFSTR_FILECONTENTS，直接通过 FileDrop 传递接收目录中的真实文件路径。
        // 限制：拖出期间文件必须存在，目标程序会立即从磁盘读取；大文件没有
        // 虚拟文件的延迟内容读取能力，源文件被移动或删除时拖放会失败。
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths.ToArray());
        // 标记来源，避免松手在窗口内时被拖入处理器当成本地文件重新入队。
        data.SetData(ReceivedFileDragFormat, "1");

        try
        {
            e.Handled = true;
            _isDraggingReceivedFile = true;
            DragDrop.DoDragDrop(
                ReceivedFilesList,
                data,
                DragDropEffects.Copy);
        }
        catch (Exception exception)
        {
            Log($"拖出接收项目失败：{exception.GetType().Name}");
        }
        finally
        {
            _isDraggingReceivedFile = false;
        }
    }

    private ReceivedProject? GetReceivedProject(DependencyObject? source)
    {
        if (source is null)
        {
            return null;
        }

        var container = ItemsControl.ContainerFromElement(
            ReceivedFilesList,
            source) as ListBoxItem;

        return container?.DataContext as ReceivedProject;
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (_isDraggingReceivedFile ||
            e.Data.GetDataPresent(ReceivedFileDragFormat))
        {
            // 从“接收”列表拖出的文件只用于拖到资源管理器，
            // 不允许作为本窗口的拖入发送来源。
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (_isDraggingReceivedFile ||
            e.Data.GetDataPresent(ReceivedFileDragFormat))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

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

        e.Effects = DragDropEffects.Copy;

        try
        {
            var plan = DroppedFileCollector.CollectPlan(droppedPaths, Log);

            if (!plan.HasWork)
            {
                Log("拖入内容中没有可发送的文件。");
                UpdateStatus("拖入内容中没有可发送的文件");
                return;
            }

            var wasRunning = _queueManager.IsRunning;
            var result = _queueManager.EnqueuePlan(plan);
            SelectFirstGroupIfNeeded(result.AddedGroups);
            LogQueueEnqueueResult(result);
            UpdateStatus(BuildQueueAddStatus(result, wasRunning));
            UpdateUi();
        }
        catch (InvalidOperationException exception)
        {
            Log($"拖入失败：{exception.Message}");
            ShowDropPrompt($"添加文件失败：{exception.Message}");
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

    /// <summary>
    /// 从资源管理器右键菜单、启动参数或第二个实例收到路径。
    /// 未连接时先暂存，连接并建立传输层后自动加入队列。
    /// </summary>
    public void ActivateFromExternalLaunch(IReadOnlyList<string> paths)
    {
        if (_isClosing)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                new Action(() => ActivateFromExternalLaunch(paths)));
            return;
        }

        ShowFromTray();

        if (!_initialized)
        {
            AddPendingExternalPaths(paths);
            return;
        }

        if (paths.Count == 0)
        {
            return;
        }

        SendTabButton.IsChecked = true;

        if (_channel is not null &&
            _queueManager is not null &&
            _step0Passed)
        {
            EnqueueExternalPaths(paths);
            return;
        }

        AddPendingExternalPaths(paths);

        if (_channel is null &&
            _role != AppRole.Sender &&
            CapabilitySupported &&
            !_isConnecting &&
            !_isScanning &&
            !_isRestartingDiscovery &&
            !_isAutoReconnecting)
        {
            RoleSenderRadio.IsChecked = true;
        }

        var message =
            $"已收到 {_pendingExternalPaths.Count} 个待发送路径，" +
            "请连接设备；连接后会自动加入发送队列。";

        Log(message);
        UpdateStatus(message);
        UpdateUi();
    }

    private void FlushPendingExternalPaths()
    {
        if (_isClosing ||
            _pendingExternalPaths.Count == 0 ||
            _channel is null ||
            _queueManager is null ||
            !_step0Passed)
        {
            return;
        }

        var paths = _pendingExternalPaths.ToArray();
        _pendingExternalPaths.Clear();
        EnqueueExternalPaths(paths);
    }

    private void EnqueueExternalPaths(IReadOnlyList<string> paths)
    {
        if (_isClosing || paths.Count == 0)
        {
            return;
        }

        if (_queueManager is not { } queue ||
            _channel is null ||
            !_step0Passed)
        {
            AddPendingExternalPaths(paths);
            return;
        }

        try
        {
            var plan = DroppedFileCollector.CollectPlan(paths, Log);

            if (!plan.HasWork)
            {
                Log("右键发送内容中没有可发送的文件。");
                UpdateStatus("右键发送内容中没有可发送的文件");
                return;
            }

            var wasRunning = queue.IsRunning;
            var result = queue.EnqueuePlan(plan);

            if (!result.HasWork &&
                result.SkippedEmptyDirectoryCount == 0)
            {
                return;
            }

            SelectFirstGroupIfNeeded(result.AddedGroups);
            SendTabButton.IsChecked = true;

            LogQueueEnqueueResult(result);
            UpdateStatus(BuildQueueAddStatus(result, wasRunning));
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

    private void AddPendingExternalPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (_pendingExternalPaths.Contains(
                    path,
                    StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            _pendingExternalPaths.Add(path);
        }
    }

    private static string? GetExecutablePath()
    {
        var processPath = Environment.ProcessPath;

        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return processPath;
        }

        try
        {
            return Process.GetCurrentProcess().MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void LogQueueEnqueueResult(QueueEnqueueResult result)
    {
        Log(
            "队列添加：" +
            $"groups={result.AddedGroups.Count}, " +
            $"files={result.Files.Count}, " +
            $"emptyDirectories={result.EmptyDirectoryCount}, " +
            $"skippedEmptyDirectories={result.SkippedEmptyDirectoryCount}。");
    }

    private static string BuildQueueAddStatus(
        QueueEnqueueResult result,
        bool wasRunning)
    {
        if (result.Files.Count == 0 && result.EmptyDirectoryCount > 0)
        {
            return $"已添加 {result.EmptyDirectoryCount} 个空文件夹，" +
                   "点击“开始传输”";
        }

        if (result.SkippedEmptyDirectoryCount > 0)
        {
            return result.Files.Count > 0
                ? $"已添加 {result.Files.Count} 个文件；" +
                  $"对端版本低于 1.3，" +
                  $"{result.SkippedEmptyDirectoryCount} 个空文件夹不会保留"
                : $"对端版本低于 1.3，" +
                  $"{result.SkippedEmptyDirectoryCount} 个空文件夹不会保留";
        }

        if (result.Files.Count > 0 && result.EmptyDirectoryCount > 0)
        {
            return wasRunning
                ? $"已追加 {result.Files.Count} 个文件，" +
                  $"另有 {result.EmptyDirectoryCount} 个空文件夹"
                : $"已添加 {result.Files.Count} 个文件，" +
                  $"另有 {result.EmptyDirectoryCount} 个空文件夹，" +
                  "点击“开始传输”";
        }

        return wasRunning
            ? $"已追加 {result.Files.Count} 个文件，当前文件完成后继续"
            : $"已添加 {result.Files.Count} 个文件，点击“开始传输”";
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

        var group = _queueManager?.FindGroup(args.File);

        if (group is not null)
        {
            QueueList.SelectedItem = group;
        }

        UpdateTransferTabHeaders();
    }

    private void QueueList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_queueManager is { } queue)
        {
            queue.SelectedGroup = QueueList.SelectedItem as QueueGroup;
            queue.SelectedFile = null;
        }

        UpdateUi();
    }

    private void QueueList_PreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(
            QueueList,
            e.OriginalSource as DependencyObject) as ListBoxItem;

        if (item is not null)
        {
            item.IsSelected = true;
            item.Focus();
        }
        else
        {
            QueueList.SelectedItem = null;
        }
    }

    private void QueueList_ContextMenuOpening(
        object sender,
        ContextMenuEventArgs e)
    {
        var group = QueueList.SelectedItem as QueueGroup;
        var queue = _queueManager;

        ViewQueueItemDetailsMenuItem.IsEnabled = group is not null;
        MoveQueueUpMenuItem.IsEnabled =
            group is not null &&
            queue?.CanMoveGroup(group, -1) == true;
        MoveQueueDownMenuItem.IsEnabled =
            group is not null &&
            queue?.CanMoveGroup(group, 1) == true;
        RemoveQueueItemMenuItem.IsEnabled =
            group is not null &&
            queue?.CanRemoveGroup(group) == true;
    }

    private void ViewQueueItemDetailsMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is not QueueGroup)
        {
            return;
        }

        QueueDetailsPanel.Visibility = Visibility.Visible;
        QueueDetailsToggleButton.Content = "收起详情";
        UpdateUi();
    }

    private void SelectFirstGroupIfNeeded(
        IReadOnlyList<QueueGroup> added)
    {
        if (QueueList.SelectedItem is null && added.Count > 0)
        {
            QueueList.SelectedItem = added[0];
        }
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

        UpdateTransferTabHeaders();
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

        UpdateTransferTabHeaders();
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

        UpdateTransferTabHeaders();
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
            $"emptyDirectories={args.EmptyDirectoryCount}, " +
            $"canceled={args.WasCanceled}");

        var hasWaiting =
            args.TotalCount >
            args.CompletedCount + args.FailedCount + args.SkippedCount;

        var emptyDirectoryText = args.EmptyDirectoryCount > 0
            ? $"，{args.EmptyDirectoryCount} 个空文件夹"
            : string.Empty;

        UpdateStatus(args.WasCanceled
            ? "队列已取消"
            : args.FailedCount > 0
                ? "队列完成，部分文件失败"
                : !string.IsNullOrWhiteSpace(args.StopReason)
                    ? $"队列已停止：{args.StopReason}"
                    : hasWaiting
                        ? "队列已结束，仍有等待文件"
                        : args.TotalCount == 0 && args.EmptyDirectoryCount > 0
                            ? $"空文件夹已创建：{args.EmptyDirectoryCount} 个"
                            : args.SkippedCount > 0
                                ? $"队列完成，{args.SkippedCount} 个文件被跳过" +
                                  emptyDirectoryText
                                : args.EmptyDirectoryCount > 0
                                    ? $"队列传输完成{emptyDirectoryText}"
                                    : "队列传输完成");

        NotifyIfInBackground(
            "发送队列",
            args.WasCanceled
                ? "队列已取消。"
                : args.FailedCount > 0
                    ? $"队列完成，{args.FailedCount} 个文件失败。"
                    : !string.IsNullOrWhiteSpace(args.StopReason)
                        ? $"队列已停止：{args.StopReason}"
                        : hasWaiting
                            ? $"队列已结束，仍有 {args.TotalCount - args.CompletedCount - args.FailedCount - args.SkippedCount} 个文件等待开始。"
                            : args.TotalCount == 0 && args.EmptyDirectoryCount > 0
                                ? $"已创建 {args.EmptyDirectoryCount} 个空文件夹。"
                                : $"队列完成，成功 {args.CompletedCount} 个文件" +
                                  emptyDirectoryText + "。",
            isError: args.FailedCount > 0 ||
                     !string.IsNullOrWhiteSpace(args.StopReason));

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
                    HandleIncomingText(args.Payload);
                    break;

                case MessageType.ControlMessage:
                {
                    var control = ControlMessageCodec.Deserialize(args.Payload.Span);

                    if (control.Subtype == ControlMessageSubtype.TextRejected)
                    {
                        Log("文本消息被拒绝：对端未开启接收远程文本。");
                        UpdateStatus("对端未开启接收文本");
                    }
                    else if (control.Subtype != ControlMessageSubtype.Ack &&
                             control.Subtype != ControlMessageSubtype.DirectoryManifest)
                    {
                        // 每个 64 KiB 块都会产生 Ack，目录清单可能拆成多条；
                        // 逐条写 UI 日志会拖垮界面。
                        Log($"控制消息：{FormatControlMessage(control)}");
                    }

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

        var reconnectPlan = TryCreateAutoReconnectPlan();

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

            if (reconnectPlan is not null)
            {
                await AutoReconnectAsync(reconnectPlan);
            }
            else
            {
                await RecoverDiscoveryAfterConnectionEndAsync("连接已断开");
            }
        }
    }

    private AutoReconnectPlan? TryCreateAutoReconnectPlan()
    {
        if (_isClosing ||
            _suppressAutoReconnect ||
            _isAutoReconnecting ||
            _role != AppRole.Sender ||
            _connectedPeer is null)
        {
            return null;
        }

        var queue = _queueManager;

        if (queue is null || !queue.IsRunning)
        {
            return null;
        }

        var files = new List<TransferSource>();

        foreach (var file in queue.Files)
        {
            if (file.Status is PendingFileStatus.Completed or PendingFileStatus.Skipped)
            {
                continue;
            }

            if (files.Any(existing =>
                    string.Equals(
                        existing.SourcePath,
                        file.SourcePath,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            files.Add(
                new TransferSource(
                    file.SourcePath,
                    file.FileName,
                    file.RelativeDirectory));
        }

        var emptyDirectories = queue.PendingEmptyDirectories;

        if (files.Count == 0 && emptyDirectories.Count == 0)
        {
            return null;
        }

        Log(
            $"准备自动重连：peer={_connectedPeer.DisplayName}, " +
            $"files={files.Count}, " +
            $"emptyDirectories={emptyDirectories.Count}。");

        return new AutoReconnectPlan(
            _connectedPeer,
            files,
            emptyDirectories);
    }

    private async Task AutoReconnectAsync(AutoReconnectPlan plan)
    {
        if (_isClosing || _isAutoReconnecting)
        {
            return;
        }

        _isAutoReconnecting = true;
        UpdateUi();
        Log($"自动重连开始：peer={plan.Peer.DisplayName}, files={plan.Files.Count}。");
        NotifyIfInBackground("连接已断开", "正在尝试自动重连…");

        var succeeded = false;

        try
        {
            for (var attempt = 0;
                 attempt < 3 && !_isClosing && !_suppressAutoReconnect;
                 attempt++)
            {
                if (attempt > 0)
                {
                    try
                    {
                        await Task.Delay(
                            TimeSpan.FromSeconds(1 << (attempt - 1)),
                            _lifetimeCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }

                UpdateStatus($"连接断开，正在自动重连（{attempt + 1}/3）…");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetimeCts.Token);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                _connectionAttemptCts = cts;
                IDuplexChannel? channel = null;

                try
                {
                    if (!await EnsureServiceStartedAsync())
                    {
                        continue;
                    }

                    channel = await _service.ConnectAsync(plan.Peer, cts.Token);
                    AttachChannel(channel);

                    if (!await RunSenderStep0Async(channel, _lifetimeCts.Token))
                    {
                        await ResetConnectionAsync();
                        continue;
                    }

                    if (!await StartMessageLayerAsync(channel))
                    {
                        await ResetConnectionAsync();
                        continue;
                    }

                    _connectedPeer = plan.Peer;
                    _connectedPeerName = plan.Peer.DisplayName;
                    var queue = _queueManager;

                    if (queue is null)
                    {
                        return;
                    }

                    var result = queue.EnqueuePlan(
                        new TransferPlan(
                            plan.Files,
                            plan.EmptyDirectories));
                    Log(
                        "自动重连成功，重新入队 " +
                        $"{result.Files.Count} 个文件，" +
                        $"{result.EmptyDirectoryCount} 个空文件夹。");
                    NotifyIfInBackground("连接已恢复", "正在继续传输。");
                    succeeded = true;
                    _isAutoReconnecting = false;
                    UpdateStatus("已重新连接，正在续传…");
                    UpdateUi();

                    await StartQueueAsync(queue);
                    return;
                }
                catch (OperationCanceledException)
                    when (_lifetimeCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    Log(
                        $"自动重连第 {attempt + 1} 次失败：" +
                        exception.GetType().Name);

                    if (channel is not null)
                    {
                        try
                        {
                            await ResetConnectionAsync();
                        }
                        catch (Exception)
                        {
                            // 重置失败不应阻止下一次重连。
                        }
                    }
                }
                finally
                {
                    if (ReferenceEquals(_connectionAttemptCts, cts))
                    {
                        _connectionAttemptCts = null;
                    }
                }
            }
        }
        finally
        {
            _isAutoReconnecting = false;

            if (!succeeded && !_isClosing)
            {
                UpdateStatus("自动重连失败，请重新扫描设备。");
                NotifyIfInBackground(
                    "自动重连失败",
                    "请重新扫描设备。",
                    isError: true);
                await RecoverDiscoveryAfterConnectionEndAsync("自动重连失败");
            }

            UpdateUi();
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
        ControlMessageSubtype.DirectoryManifest =>
            $"DirectoryManifest, entries={message.Directories?.Count ?? 0}",
        _ => message.Subtype.ToString()
    };

    private void AddQueueFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSending || _queueManager is not { } queue)
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
            var plan = DroppedFileCollector.CollectPlan(
                dialog.FileNames,
                Log);
            var wasRunning = queue.IsRunning;
            var result = queue.EnqueuePlan(plan);

            if (!result.HasWork &&
                result.SkippedEmptyDirectoryCount == 0)
            {
                return;
            }

            SelectFirstGroupIfNeeded(result.AddedGroups);
            LogQueueEnqueueResult(result);
            UpdateStatus(BuildQueueAddStatus(result, wasRunning));
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

    private void AddQueueFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSending || _queueManager is not { } queue)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "选择要发送的文件夹"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var plan = DroppedFileCollector.CollectPlan(
                new[] { dialog.FolderName },
                Log);

            if (!plan.HasWork)
            {
                UpdateStatus("所选文件夹中没有可发送的内容");
                return;
            }

            var wasRunning = queue.IsRunning;
            var result = queue.EnqueuePlan(plan);

            if (!result.HasWork &&
                result.SkippedEmptyDirectoryCount == 0)
            {
                return;
            }

            SelectFirstGroupIfNeeded(result.AddedGroups);
            LogQueueEnqueueResult(result);
            UpdateStatus(BuildQueueAddStatus(result, wasRunning));
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

    private void PauseQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_queueManager is not { } queue || !queue.IsRunning)
        {
            return;
        }

        if (queue.IsPaused)
        {
            queue.Resume();
            UpdateStatus("正在继续传输…");
        }
        else
        {
            queue.Pause();
            UpdateStatus("已暂停；点击“继续”恢复传输");
        }

        UpdateUi();
    }

    private void MoveQueueUpMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MoveQueueSelection(-1);
    }

    private void MoveQueueDownMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MoveQueueSelection(1);
    }

    private void RemoveQueueItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_queueManager is not { } queue ||
            QueueList.SelectedItem is not QueueGroup group)
        {
            return;
        }

        if (queue.RemoveGroup(group))
        {
            Log($"已从队列移除：{group.DisplayName}");
            UpdateUi();
        }
    }

    private void MoveQueueSelection(int offset)
    {
        if (_queueManager is not { } queue ||
            QueueList.SelectedItem is not QueueGroup group)
        {
            return;
        }

        if (queue.MoveGroup(group, offset))
        {
            UpdateUi();
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

        await StartQueueAsync(queue);
    }

    private async Task StartQueueAsync(TransferQueueManager queue)
    {
        if (_channel is null ||
            _dispatcher is null ||
            !_step0Passed ||
            _isSending ||
            !queue.CanStart)
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
            var progress = new ThrottledProgress<FileTransferProgress>(
                value => UpdateStatus(
                    $"正在发送：{value.BytesTransferred}/{value.TotalBytes} 字节"),
                TimeSpan.FromMilliseconds(150),
                value => value.TotalBytes > 0 &&
                         value.BytesTransferred >= value.TotalBytes);

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
            _connectedPeer = null;
            SendPanel.DataContext = null;
            QueueDetailsPanel.Visibility = Visibility.Collapsed;
            QueueDetailsToggleButton.Content = "详情";
            _clipboardSyncService.SetSender(null);
            ClearCurrentReceive();

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
        _trayIcon.BalloonClicked += ShowFromTray;
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
        _textInboxWindow?.Close();
        _textInboxWindow = null;
        Hide();
        _isHiddenToTray = true;
        Log("窗口已隐藏到托盘，传输继续在后台进行。");

        if (_notificationsEnabled)
        {
            _trayIcon?.ShowBalloon(
                "后台运行中",
                "应用仍在后台保持连接和传输。双击托盘图标可恢复窗口。");
        }
    }

    private bool HasBackgroundWork =>
        _isAutoReconnecting ||
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

        if (!_notificationsEnabled)
        {
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

    private void ChangeReceiveDirectoryButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_isClosing || _channel is not null)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "选择接收目录",
            InitialDirectory = Directory.Exists(_receiveDirectory)
                ? _receiveDirectory
                : GetDefaultReceiveDirectory()
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var selected = dialog.FolderName.Trim();

        if (string.IsNullOrEmpty(selected))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(selected);
            selected = Path.GetFullPath(selected);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"无法使用该目录：{exception.Message}",
                "接收目录",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        _receiveDirectory = selected;
        UpdateReceiveDirectoryText();
        UpdateUi();

        var saved = SaveReceiverPreferences();
        Log(
            $"接收目录已更改：{selected}" +
            (saved ? string.Empty : "（保存失败）"));
        UpdateStatus("接收目录已更新");
    }

    private void UpdateReceiveDirectoryText()
    {
        ReceiveDirectoryText.Text = $"接收目录：{_receiveDirectory}";
    }

    private async void SendTextButton_Click(object sender, RoutedEventArgs e)
    {
        var dispatcher = _dispatcher;

        if (dispatcher is null || !_messageLayerReady || _channel is null)
        {
            return;
        }

        var dialog = new SendTextWindow
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var text = dialog.Text;

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            var payload = TextMessageCodec.Encode(text);

            await dispatcher.SendAsync(
                MessageType.TextMessage,
                payload,
                _lifetimeCts.Token);

            Log($"文本已发送：{payload.Length} 字节");
            UpdateStatus("文本已发送");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // 窗口关闭或连接重置。
        }
        catch (Exception exception)
        {
            var message = LogException(
                exception,
                WifiDirectOperation.Transfer,
                WifiDirectErrorContext.WinRt,
                _lifetimeCts.Token);
            UpdateStatus(message);
        }
    }

    private void TextInboxButton_Click(object sender, RoutedEventArgs e)
    {
        if (_textInboxWindow is { IsLoaded: true } existing)
        {
            if (!existing.IsVisible)
            {
                existing.Show();
            }

            existing.Activate();
            existing.Refresh();
            return;
        }

        var window = new TextInboxWindow(_textInbox)
        {
            Owner = this
        };

        window.InboxChanged += (_, _) => UpdateTextInboxButton();
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_textInboxWindow, window))
            {
                _textInboxWindow = null;
            }

            UpdateTextInboxButton();
        };

        _textInboxWindow = window;
        window.Show();
        window.Refresh();
    }

    private void UpdateTextInboxButton()
    {
        var unread = _textInbox.UnreadCount;
        TextInboxButton.Content = unread > 0
            ? $"收到的文本 ({unread})"
            : "收到的文本";
    }

    private void HandleIncomingText(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > TextMessageCodec.MaxPayloadBytes)
        {
            Log("收到文本消息但超过 64 KiB 上限，已忽略。");
            _ = SendTextRejectedAsync();
            return;
        }

        string text;

        try
        {
            text = TextMessageCodec.Decode(payload.Span);
        }
        catch (Exception exception)
        {
            Log($"文本消息无效：{exception.Message}");
            return;
        }

        if (!_textReceiveEnabled)
        {
            Log("收到文本消息，但本机未开启接收远程文本。");
            _ = SendTextRejectedAsync();
            UpdateStatus("收到文本消息，但本机未开启接收");
            NotifyIfInBackground(
                "文本消息",
                "收到文本消息，但本机未开启接收远程文本。");
            return;
        }

        _ = Dispatcher.BeginInvoke(
            new Action(() => AddIncomingText(text)));
    }

    private async Task SendTextRejectedAsync()
    {
        var dispatcher = _dispatcher;

        if (dispatcher is null)
        {
            return;
        }

        try
        {
            await dispatcher.SendAsync(
                MessageType.ControlMessage,
                ControlMessageCodec.SerializeTextRejected(),
                CancellationToken.None);
        }
        catch (Exception)
        {
            // 连接已断开时无法回执。
        }
    }

    private void AddIncomingText(string text)
    {
        var sender = string.IsNullOrWhiteSpace(_connectedPeerName)
            ? "对端"
            : _connectedPeerName!;

        _textInbox.Add(
            new ReceivedTextMessage(
                DateTimeOffset.Now,
                sender,
                text));

        UpdateTextInboxButton();
        _textInboxWindow?.Refresh();

        Log($"收到文本消息：{text.Length} 字符，来自 {sender}");
        UpdateStatus("收到新的文本消息");
        NotifyIfInBackground(
            "收到文本",
            $"来自 {sender} 的文本消息。");
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
        _uiLogLineCount = 0;
    }

    private void LogToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SetLogCollapsed(!_isLogCollapsed);
    }

    private void QueueDetailsToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (QueueDetailsPanel is null)
        {
            return;
        }

        var show = QueueDetailsPanel.Visibility != Visibility.Visible;

        QueueDetailsPanel.Visibility =
            show ? Visibility.Visible : Visibility.Collapsed;
        QueueDetailsToggleButton.Content = show ? "收起详情" : "详情";
    }

    private void AdvancedSettingsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (AdvancedSettingsPanel is null)
        {
            return;
        }

        var show = AdvancedSettingsToggle.IsChecked == true;

        AdvancedSettingsPanel.Visibility =
            show ? Visibility.Visible : Visibility.Collapsed;
        AdvancedSettingsChevron.Text = show ? "⌃" : "⌄";

        if (show)
        {
            _ = Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    var position = AdvancedSettingsToggle.TranslatePoint(
                        new Point(0, 0),
                        LeftRailScrollViewer);
                    var target = LeftRailScrollViewer.VerticalOffset +
                                 position.Y - 8;

                    LeftRailScrollViewer.ScrollToVerticalOffset(
                        Math.Max(0, target));
                }),
                DispatcherPriority.Background);
        }
        else
        {
            LeftRailScrollViewer.ScrollToTop();
        }
    }

    private void SetLogCollapsed(bool collapsed)
    {
        _isLogCollapsed = collapsed;

        if (collapsed)
        {
            LogRow.MinHeight = 0;
            LogRow.Height = GridLength.Auto;
            LogTextBox.Visibility = Visibility.Collapsed;
            LogToggleButton.Content = "展开";
        }
        else
        {
            LogRow.MinHeight = 120;
            LogRow.Height = new GridLength(ExpandedLogHeight);
            LogTextBox.Visibility = Visibility.Visible;
            LogToggleButton.Content = "折叠";
            LogTextBox.ScrollToEnd();
        }
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new HistoryWindow(_historyStore)
        {
            Owner = this
        };

        window.ShowDialog();
    }

    private void ExportDiagnosticsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        string zipPath;

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "WifiDirectTransfer",
                "diagnostics");
            Directory.CreateDirectory(directory);

            zipPath = Path.Combine(
                directory,
                $"WifiDirectTransfer-diag-{DateTime.Now:yyyyMMdd-HHmmss-fff}.zip");

            var logDirectory = Path.GetDirectoryName(AppLog.FilePath);
            var files = new List<string>
            {
                AppLog.FilePath,
                StartupLog.FilePath
            };

            if (!string.IsNullOrWhiteSpace(logDirectory))
            {
                files.Add(Path.Combine(logDirectory, "app.prev.log"));
            }

            files.Add(_receiverPreferencesStore.FilePath);

            DiagnosticsPackage.Create(
                zipPath,
                BuildDiagnosticsInfo(),
                files);

            Log($"诊断信息已导出：{zipPath}");
        }
        catch (Exception exception)
        {
            Log($"导出诊断失败：{exception.GetType().Name}");
            MessageBox.Show(
                this,
                $"导出失败：{exception.Message}",
                "导出诊断",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var openFolder = MessageBox.Show(
            this,
            $"诊断信息已保存到：\n{zipPath}\n\n是否打开所在文件夹？",
            "导出诊断",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (openFolder != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Process.Start(
                new ProcessStartInfo(
                    "explorer.exe",
                    $"/select,\"{zipPath}\"")
                {
                    UseShellExecute = true
                });
        }
        catch (Exception)
        {
            // 打开资源管理器失败不影响导出结果。
        }
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new AboutWindow(BuildDiagnosticsInfo())
        {
            Owner = this
        };

        window.ShowDialog();
    }

    private string BuildDiagnosticsInfo()
    {
        return
            $"生成时间：{DateTimeOffset.Now:O}{Environment.NewLine}" +
            $"本端构建：{GetBuildInfo()}{Environment.NewLine}" +
            $"协议版本：{MessageProtocol.ProtocolVersion}{Environment.NewLine}" +
            $"对端构建：{_peerBuild ?? "未连接"}{Environment.NewLine}" +
            $"接收目录：{_receiveDirectory}{Environment.NewLine}" +
            $"同名文件策略：{GetSameNamePolicyText(_sameNamePolicy)}{Environment.NewLine}" +
            $"剪贴板同步：{(_clipboardSyncEnabled ? "已启用" : "已关闭")}{Environment.NewLine}" +
            $"接收远程文本：{(_textReceiveEnabled ? "已启用" : "已关闭")}{Environment.NewLine}" +
            $"文本收件箱：{_textInbox.Messages.Count} 条，{_textInbox.UnreadCount} 条未读{Environment.NewLine}" +
            $"连接状态：{(_channel is null ? "未连接" : "已连接")}{Environment.NewLine}" +
            $"系统：{RuntimeInformation.OSDescription}{Environment.NewLine}" +
            $".NET：{RuntimeInformation.FrameworkDescription}{Environment.NewLine}" +
            $"进程：{(Environment.Is64BitProcess ? "x64" : "x86")}{Environment.NewLine}" +
            $"日志：{AppLog.FilePath}";
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

    private void TransferTab_Checked(object sender, RoutedEventArgs e)
    {
        if (SendPanel is null || ReceivePanel is null)
        {
            return;
        }

        var showSend = ReferenceEquals(sender, SendTabButton);

        SendPanel.Visibility =
            showSend ? Visibility.Visible : Visibility.Collapsed;
        ReceivePanel.Visibility =
            showSend ? Visibility.Collapsed : Visibility.Visible;

        if (WorkspaceHintText is not null)
        {
            WorkspaceHintText.Text = showSend
                ? "拖入文件即可加入发送队列"
                : "接收完成后可直接拖出到资源管理器";
        }

        if (!showSend)
        {
            RefreshReceivedFilesAvailability();
        }
    }

    private void UpdateTransferTabHeaders()
    {
        if (_isClosing)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                new Action(UpdateTransferTabHeaders));
            return;
        }

        if (SendTabButton is null || ReceiveTabButton is null)
        {
            return;
        }

        var sendText = "发送";
        var queue = _queueManager;

        if (queue is not null)
        {
            if (queue.IsRunning)
            {
                sendText = "发送 · 进行中";
            }
            else if (queue.HasGroups)
            {
                sendText = $"发送 · {queue.GroupCount}";
            }
        }

        SendTabButton.Content = sendText;

        var receiveText = "接收";

        if (_currentReceiveItem is not null)
        {
            receiveText =
                $"接收 · {(int)Math.Round(_currentReceiveItem.ProgressPercent)}%";
        }
        else if (_receivedProjects.Projects.Count > 0)
        {
            receiveText = $"接收 · {_receivedProjects.Projects.Count}";
        }

        ReceiveTabButton.Content = receiveText;
    }

    private void UpdateReceiveProgress(FileTransferProgress value)
    {
        if (_isClosing || _currentReceiveItem is null)
        {
            return;
        }

        _currentReceiveItem.UpdateProgress(value.BytesTransferred);

        UpdateStatus(
            $"正在接收：{_currentReceiveItem.FileName} " +
            $"{FormatBytes(value.BytesTransferred)} / {FormatBytes(value.TotalBytes)}");

        UpdateTransferTabHeaders();
    }

    private void ClearCurrentReceive()
    {
        if (_isClosing)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                new Action(ClearCurrentReceive));
            return;
        }

        _currentReceiveItem = null;
        CurrentReceivePanel.DataContext = null;
        CurrentReceivePanel.Visibility = Visibility.Collapsed;
        _receivedProjects.ClearReceiving(DateTimeOffset.Now);
        UpdateTransferTabHeaders();
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
        var busy = _isScanning ||
                   _isConnecting ||
                   _isHandlingRequest ||
                   _isRestartingDiscovery ||
                   _isAutoReconnecting;

        RoleReceiverRadio.IsEnabled = supported && !connected && !busy;
        RoleSenderRadio.IsEnabled = supported && !connected && !busy;

        SameNamePolicyPanel.Visibility = Visibility.Visible;
        SameNamePolicyComboBox.IsEnabled = true;

        var waiting = _serviceStarted && _role == AppRole.Receiver && !connected;
        WaitButton.Content = waiting ? "重新等待" : "等待连接";
        WaitButton.IsEnabled = supported &&
                               _role == AppRole.Receiver &&
                               !connected &&
                               !_isHandlingRequest &&
                               !_isRestartingAdvertisement;

        UpdateAdvertisementWatchdog(
            waiting && !_isRestartingAdvertisement);

        ScanButton.IsEnabled = supported &&
                               _role == AppRole.Sender &&
                               !connected &&
                               !_isScanning &&
                               !_isConnecting &&
                               !_isRestartingDiscovery &&
                               !_isAutoReconnecting;

        // 不禁用整个列表：禁用 ListBox 时 WPF 会用系统浅色绘制禁用项，
        // 深色主题下会出现浅色卡片。连接按钮仍会按角色/状态单独禁用。
        PeerList.IsEnabled = supported;

        ConnectButton.IsEnabled = supported &&
                                  _role == AppRole.Sender &&
                                  !connected &&
                                  !_isScanning &&
                                  !_isConnecting &&
                                  !_isRestartingDiscovery &&
                                  !_isAutoReconnecting &&
                                  PeerList.SelectedItem is PeerListItem { IsDiscovered: true };

        QuickReconnectButton.IsEnabled = supported &&
                                         _role == AppRole.Sender &&
                                         !connected &&
                                         !_isScanning &&
                                         !_isConnecting &&
                                         !_isRestartingDiscovery &&
                                         !_isAutoReconnecting &&
                                         PeerList.SelectedItem is PeerListItem { IsKnown: true };

        var queue = _queueManager;
        var queueRunning = queue?.IsRunning == true;
        var canTransfer = supported &&
                          connected &&
                          _messageLayerReady &&
                          _dispatcher is not null &&
                          _step0Passed &&
                          !busy;

        SendTestFileButton.IsEnabled = canTransfer &&
                                       !_isSending &&
                                       !queueRunning;

        SendTextButton.IsEnabled = canTransfer;
        TextInboxButton.IsEnabled = true;

        QueueEmptyPanel.Visibility = queue is { HasGroups: true }
            ? Visibility.Collapsed
            : Visibility.Visible;

        QueueEmptyText.Text = "队列为空";
        QueueEmptyHintText.Text =
            "拖入文件或文件夹，或从资源管理器添加";

        AddQueueFilesButton.IsEnabled = canTransfer &&
                                        !_isSending &&
                                        queue is not null;
        AddQueueFolderButton.IsEnabled = AddQueueFilesButton.IsEnabled;

        PauseQueueButton.Content = queue?.IsPaused == true ? "继续" : "暂停";
        PauseQueueButton.IsEnabled = queue is { IsRunning: true };

        StartQueueButton.IsEnabled = canTransfer &&
                                     !_isSending &&
                                     !queueRunning &&
                                     queue?.CanStart == true;

        ClearQueueButton.IsEnabled = canTransfer &&
                                     !_isSending &&
                                     !queueRunning &&
                                     queue?.HasGroups == true;

        ChangeReceiveDirectoryButton.IsEnabled = !connected && !busy;

        UpdateConnectionHeader();
        UpdateTransferTabHeaders();

        ScanButton.Content = _isScanning ? "搜索中…" : "扫描设备";
    }

    private void UpdateConnectionHeader()
    {
        var connected = _channel is not null;

        ConnectionChip.Background = connected
            ? Brushes.Honeydew
            : Brushes.WhiteSmoke;
        ConnectionDot.Fill = connected
            ? Brushes.SeaGreen
            : _isConnecting
                ? Brushes.DarkOrange
                : Brushes.Gray;
        ConnectionStatusText.Foreground = connected
            ? Brushes.ForestGreen
            : Brushes.DimGray;

        ConnectionStatusText.Text = connected
            ? "已连接"
            : _isConnecting
                ? "连接中…"
                : _isScanning
                    ? "搜索中…"
                    : "未连接";

        ConnectionPeerText.Text = connected
            ? _connectedPeerName ?? "对端设备"
            : "选择设备并连接";

        ConnectionHintText.Visibility =
            connected ? Visibility.Collapsed : Visibility.Visible;

        DisconnectButton.Visibility =
            connected &&
            !_isConnecting &&
            !_isHandlingRequest &&
            !_isDisconnecting
                ? Visibility.Visible
                : Visibility.Collapsed;
        DisconnectButton.IsEnabled =
            connected &&
            !_isConnecting &&
            !_isHandlingRequest &&
            !_isDisconnecting;
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
        _uiLogLineCount++;

        if (_uiLogLineCount > MaxUiLogLines)
        {
            TrimLogTextBox();
        }

        LogTextBox.ScrollToEnd();
    }

    private void TrimLogTextBox()
    {
        var removeLines = Math.Min(
            _uiLogLineCount - MaxUiLogLines + UiLogTrimBatch,
            _uiLogLineCount);

        if (removeLines <= 0)
        {
            return;
        }

        var text = LogTextBox.Text;
        var index = 0;

        for (var line = 0; line < removeLines; line++)
        {
            var next = text.IndexOf('\n', index);

            if (next < 0)
            {
                index = text.Length;
                break;
            }

            index = next + 1;
        }

        if (index <= 0)
        {
            return;
        }

        LogTextBox.Text = text[index..];
        _uiLogLineCount = Math.Max(0, _uiLogLineCount - removeLines);
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

    private static string ResolveReceiveDirectory(string? saved)
    {
        if (!string.IsNullOrWhiteSpace(saved))
        {
            try
            {
                var fullPath = Path.GetFullPath(saved);

                if (Path.IsPathRooted(fullPath))
                {
                    return fullPath;
                }
            }
            catch (Exception)
            {
                // 无法解析时回退到默认目录。
            }
        }

        return GetDefaultReceiveDirectory();
    }

    private static string GetDefaultReceiveDirectory()
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

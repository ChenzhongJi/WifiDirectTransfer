using System.Windows;
using System.Windows.Threading;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

public partial class DevicePairingWindow : Window
{
    private const int TimeoutSeconds = 30;

    private readonly DispatcherTimer _timer;
    private int _secondsRemaining = TimeoutSeconds;

    public DevicePairingWindow(
        PeerDeviceIdentity peer,
        bool alreadyTrusted = false)
    {
        ArgumentNullException.ThrowIfNull(peer);

        InitializeComponent();

        PeerNameText.Text = string.IsNullOrWhiteSpace(peer.DisplayName)
            ? "未命名设备"
            : peer.DisplayName;
        FingerprintText.Text =
            DeviceIdentityCrypto.FormatFingerprint(peer.Fingerprint);
        PairingCodeText.Text = peer.PairingCode;

        if (alreadyTrusted)
        {
            TrustCheckBox.Content = "此设备已受信任";
            TrustCheckBox.IsChecked = true;
            TrustCheckBox.IsEnabled = false;
            TrustHelpText.Text =
                "对方尚未信任本机，因此仍显示配对码供双方确认。";
        }

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        _timer.Tick += OnTimerTick;
        Loaded += OnLoaded;
        Closed += OnClosed;

        UpdateCountdown();
    }

    public bool TimedOut { get; private set; }

    public bool TrustRequested => TrustCheckBox.IsChecked == true;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateCountdown();
        _timer.Start();
        AcceptButton.Focus();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _secondsRemaining--;

        if (_secondsRemaining <= 0)
        {
            TimedOut = true;
            _timer.Stop();
            DialogResult = false;
            return;
        }

        UpdateCountdown();
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        DialogResult = true;
    }

    private void RejectButton_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        DialogResult = false;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
    }

    private void UpdateCountdown()
    {
        CountdownText.Text = $"{_secondsRemaining} 秒后自动拒绝";
    }
}

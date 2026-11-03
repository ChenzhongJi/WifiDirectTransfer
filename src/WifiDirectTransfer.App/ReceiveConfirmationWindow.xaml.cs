using System.Windows;
using System.Windows.Threading;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

public partial class ReceiveConfirmationWindow : Window
{
    private const int TimeoutSeconds = 15;

    private readonly DispatcherTimer _timer;
    private int _secondsRemaining = TimeoutSeconds;

    public ReceiveConfirmationWindow(WifiDirectPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);

        InitializeComponent();

        SenderNameText.Text = peer.DisplayName;
        SenderIdText.Text = peer.Id;

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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateCountdown();
        _timer.Start();
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

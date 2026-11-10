using System.Windows;
using System.Windows.Controls;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

internal sealed class TrustedDeviceItem
{
    public TrustedDeviceItem(TrustedDevice device)
    {
        Device = device;
    }

    public TrustedDevice Device { get; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Device.DisplayName)
            ? Device.LastPeerName
            : Device.DisplayName;

    public string FingerprintText =>
        DeviceIdentityCrypto.FormatFingerprint(Device.Fingerprint);

    public string LastConnectedText =>
        Device.LastConnectedAt == default
            ? "—"
            : Device.LastConnectedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public partial class TrustedDevicesWindow : Window
{
    private readonly TrustedDeviceStore _store;

    public TrustedDevicesWindow(TrustedDeviceStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        InitializeComponent();
        RefreshList();
    }

    public event EventHandler<TrustedDevice>? TrustRemoved;

    private void RefreshList()
    {
        var devices = _store.Load();
        TrustedDeviceList.ItemsSource = devices
            .Select(device => new TrustedDeviceItem(device))
            .ToArray();
        CountText.Text = $"{devices.Count} 台设备";
        EmptyText.Visibility = devices.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        RemoveButton.IsEnabled = false;
    }

    private void TrustedDeviceList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        RemoveButton.IsEnabled =
            TrustedDeviceList.SelectedItem is TrustedDeviceItem;
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (TrustedDeviceList.SelectedItem is not TrustedDeviceItem item)
        {
            return;
        }

        if (!_store.Remove(item.Device.Fingerprint))
        {
            StatusText.Text = "取消信任失败，请检查本地存储权限。";
            return;
        }

        StatusText.Text =
            $"已取消信任 {item.DisplayName}。下次连接需要重新配对。";
        TrustRemoved?.Invoke(this, item.Device);
        RefreshList();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

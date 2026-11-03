using System.Windows;

namespace WifiDirectTransfer.App;

public partial class ClipboardSyncConfirmationWindow : Window
{
    public ClipboardSyncConfirmationWindow()
    {
        InitializeComponent();
    }

    private void EnableButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

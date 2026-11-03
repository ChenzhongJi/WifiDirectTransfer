using System.Windows;

namespace WifiDirectTransfer.App;

internal enum BackgroundCloseChoice
{
    Cancel,
    Background,
    Exit
}

public partial class BackgroundCloseWindow : Window
{
    public BackgroundCloseWindow()
    {
        InitializeComponent();
    }

    internal BackgroundCloseChoice Choice { get; private set; } =
        BackgroundCloseChoice.Cancel;

    private void BackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = BackgroundCloseChoice.Background;
        DialogResult = true;
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = BackgroundCloseChoice.Exit;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = BackgroundCloseChoice.Cancel;
        DialogResult = false;
    }
}

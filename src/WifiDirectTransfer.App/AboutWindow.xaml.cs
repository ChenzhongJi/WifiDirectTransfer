using System.Windows;

namespace WifiDirectTransfer.App;

public partial class AboutWindow : Window
{
    private readonly string _details;

    public AboutWindow(string details)
    {
        ArgumentNullException.ThrowIfNull(details);

        _details = details;
        InitializeComponent();
        DetailsTextBox.Text = details;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_details);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"复制失败：{exception.Message}",
                "关于",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}

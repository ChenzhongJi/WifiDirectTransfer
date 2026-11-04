using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

public partial class SendTextWindow : Window
{
    public SendTextWindow()
    {
        InitializeComponent();

        Loaded += (_, _) => TextTextBox.Focus();
        UpdateCounter();
    }

    public string Text => TextTextBox.Text;

    private void TextTextBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        UpdateCounter();
    }

    private void UpdateCounter()
    {
        var text = TextTextBox.Text ?? string.Empty;
        var bytes = Encoding.UTF8.GetByteCount(text);
        var overLimit = bytes > TextMessageCodec.MaxPayloadBytes;

        ByteCountText.Text =
            $"{bytes} / {TextMessageCodec.MaxPayloadBytes} 字节";
        ByteCountText.Foreground = overLimit
            ? Brushes.Firebrick
            : Brushes.Gray;
        SendButton.IsEnabled = text.Length > 0 && !overLimit;
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SendButton.IsEnabled)
        {
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

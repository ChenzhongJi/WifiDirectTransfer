using System.Windows;
using System.Windows.Controls;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

internal sealed record TextInboxItem(ReceivedTextMessage Message)
{
    public string HeaderText =>
        $"{Message.ReceivedAt.ToLocalTime():HH:mm:ss}  ·  来自 {Message.SenderName}";

    public string Text => Message.Text;
}

public partial class TextInboxWindow : Window
{
    private readonly TextInbox _inbox;

    public TextInboxWindow(TextInbox inbox)
    {
        ArgumentNullException.ThrowIfNull(inbox);

        _inbox = inbox;
        InitializeComponent();
        Reload();
    }

    public event EventHandler? InboxChanged;

    public void Refresh()
    {
        Reload();
    }

    private void Reload()
    {
        var items = _inbox
            .Messages
            .Select(message => new TextInboxItem(message))
            .ToArray();

        MessagesList.ItemsSource = items;
        EmptyText.Visibility =
            items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        _inbox.MarkAllRead();
        InboxChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TextInboxItem item })
        {
            return;
        }

        try
        {
            Clipboard.SetText(item.Text);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"复制失败：{exception.Message}",
                "文本消息",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "确定清空所有文本消息吗？",
            "文本消息",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _inbox.Clear();
        Reload();
    }
}

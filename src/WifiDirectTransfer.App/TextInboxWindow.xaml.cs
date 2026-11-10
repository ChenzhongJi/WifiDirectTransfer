using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

internal sealed record TextInboxItem(ReceivedTextMessage Message)
{
    public string HeaderText =>
        $"{Message.ReceivedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  来自 {Message.SenderName}";

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
        ClearButton.IsEnabled = items.Length > 0;

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
            MessageDialog.ShowError(
                this,
                "文本消息",
                $"复制失败：{exception.Message}");
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (!MessageDialog.Confirm(
                this,
                "文本消息",
                "确定清空所有文本消息吗？"))
        {
            return;
        }

        _inbox.Clear();
        Reload();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();

    private void TextInboxWindow_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close();
    }
}

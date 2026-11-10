using System.Windows;
using System.Windows.Controls;

namespace WifiDirectTransfer.App;

public enum MessageDialogKind
{
    Information,
    Warning,
    Error
}

public partial class MessageDialogWindow : Window
{
    public MessageDialogWindow(
        string title,
        string message,
        MessageDialogKind kind,
        bool showCancelButton,
        bool defaultYes)
    {
        InitializeComponent();

        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;

        var borderBrushKey = kind switch
        {
            MessageDialogKind.Warning => "WarningBrush",
            MessageDialogKind.Error => "DangerBrush",
            _ => "AccentBrush"
        };

        MessageBorder.SetResourceReference(
            Border.BorderBrushProperty,
            borderBrushKey);

        if (showCancelButton)
        {
            OkButton.Content = "是";
            NoButton.Visibility = Visibility.Visible;
            NoButton.IsCancel = true;
            NoButton.IsDefault = !defaultYes;
            OkButton.IsDefault = defaultYes;
        }
        else
        {
            OkButton.Content = "确定";
            NoButton.Visibility = Visibility.Collapsed;
            NoButton.IsDefault = false;
            OkButton.IsDefault = true;
            OkButton.IsCancel = true;
        }

        Loaded += OnLoaded;
    }

    public bool? Result { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (OkButton.IsDefault)
        {
            OkButton.Focus();
        }
        else
        {
            NoButton.Focus();
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Result = true;
        DialogResult = true;
    }

    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        Result = false;
        DialogResult = false;
    }
}

public static class MessageDialog
{
    public static void ShowInfo(
        Window? owner,
        string title,
        string message)
    {
        Show(owner, title, message, MessageDialogKind.Information);
    }

    public static void ShowError(
        Window? owner,
        string title,
        string message)
    {
        Show(owner, title, message, MessageDialogKind.Error);
    }

    public static bool Confirm(
        Window? owner,
        string title,
        string message,
        bool defaultNo = false,
        MessageDialogKind kind = MessageDialogKind.Warning)
    {
        var dialog = new MessageDialogWindow(
            title,
            message,
            kind,
            showCancelButton: true,
            defaultYes: !defaultNo)
        {
            Owner = owner
        };

        if (owner is null)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true;
    }

    private static void Show(
        Window? owner,
        string title,
        string message,
        MessageDialogKind kind)
    {
        var dialog = new MessageDialogWindow(
            title,
            message,
            kind,
            showCancelButton: false,
            defaultYes: true)
        {
            Owner = owner
        };

        if (owner is null)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();
    }
}

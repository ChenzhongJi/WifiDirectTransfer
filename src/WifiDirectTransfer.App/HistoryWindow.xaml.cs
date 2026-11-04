using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

internal sealed class HistoryItem
{
    public HistoryItem(TransferHistoryEntry entry)
    {
        Entry = entry;
    }

    public TransferHistoryEntry Entry { get; }

    public TransferOutcome Outcome => Entry.Outcome;

    public string TimeText =>
        Entry.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public string DirectionText =>
        Entry.Direction == TransferDirection.Send ? "发送" : "接收";

    public string FileName => Entry.FileName;

    public string SizeText =>
        Entry.FileSize > 0 ? FormatBytes(Entry.FileSize) : "—";

    public string OutcomeText => Entry.Outcome switch
    {
        TransferOutcome.Completed => "完成",
        TransferOutcome.Failed => "失败",
        TransferOutcome.Skipped => "跳过",
        _ => Entry.Outcome.ToString()
    };

    public string PeerText =>
        string.IsNullOrWhiteSpace(Entry.PeerName) ? "—" : Entry.PeerName;

    public string? FilePath => Entry.FilePath;

    public bool HasPath => !string.IsNullOrWhiteSpace(Entry.FilePath);

    public string DetailText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Entry.ErrorMessage))
            {
                return Entry.ErrorMessage!;
            }

            return Entry.FilePath ?? string.Empty;
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} 字节",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.0} GB"
    };
}

public partial class HistoryWindow : Window
{
    private readonly TransferHistoryStore _store;

    public HistoryWindow(TransferHistoryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        var items = _store
            .Load()
            .Select(entry => new HistoryItem(entry))
            .ToArray();

        HistoryList.ItemsSource = items;
        CountText.Text = $"共 {items.Length} 条记录";
        EmptyText.Visibility =
            items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.IsEnabled = items.Length > 0;
        DetailText.Text = string.Empty;
        OpenFolderButton.IsEnabled = false;
    }

    private void HistoryList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        var item = HistoryList.SelectedItem as HistoryItem;

        OpenFolderButton.IsEnabled = item?.HasPath == true;
        DetailText.Text = item?.DetailText ?? string.Empty;
    }

    private void HistoryList_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryItem { HasPath: true } item)
        {
            OpenInExplorer(item.FilePath);
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryItem { HasPath: true } item)
        {
            OpenInExplorer(item.FilePath);
        }
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "确定清空所有传输历史吗？此操作不会删除已传输的文件。",
            "传输历史",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _store.Clear();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"清空历史失败：{exception.Message}",
                "传输历史",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        Reload();
    }

    private void OpenInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (!File.Exists(path))
            {
                MessageBox.Show(
                    this,
                    "文件已不存在，可能已被移动或删除。",
                    "传输历史",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            Process.Start(
                new ProcessStartInfo(
                    "explorer.exe",
                    $"/select,\"{path}\"")
                {
                    UseShellExecute = true
                });
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"无法打开文件夹：{exception.Message}",
                "传输历史",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}

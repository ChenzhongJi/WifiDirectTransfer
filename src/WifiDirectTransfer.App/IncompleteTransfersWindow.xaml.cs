using System.Windows;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

internal sealed record IncompleteTransferItem(
    string FileName,
    string ProgressText,
    string TimeText);

public partial class IncompleteTransfersWindow : Window
{
    public IncompleteTransfersWindow(
        IReadOnlyList<IncompleteTransfer> transfers)
    {
        ArgumentNullException.ThrowIfNull(transfers);

        InitializeComponent();

        HeaderText.Text = $"发现 {transfers.Count} 个未完成传输";
        TransferList.ItemsSource = transfers
            .Select(transfer => new IncompleteTransferItem(
                transfer.FileName,
                CreateProgressText(transfer),
                $"最后写入：{transfer.LastWriteTime:yyyy-MM-dd HH:mm}"))
            .ToArray();
    }

    internal bool DeleteAll { get; private set; }

    private void KeepButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteAll = false;
        DialogResult = true;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteAll = true;
        DialogResult = true;
    }

    private static string CreateProgressText(IncompleteTransfer transfer)
    {
        if (transfer.ExpectedBytes is not > 0)
        {
            return $"已接收 {FormatBytes(transfer.ReceivedBytes)}，总大小未知";
        }

        var expected = transfer.ExpectedBytes.Value;
        var percent = Math.Clamp(
            transfer.ReceivedBytes * 100.0 / expected,
            0,
            100);

        return
            $"{FormatBytes(transfer.ReceivedBytes)} / {FormatBytes(expected)}" +
            $"（{percent:0}%）";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} 字节",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.0} GB"
    };
}

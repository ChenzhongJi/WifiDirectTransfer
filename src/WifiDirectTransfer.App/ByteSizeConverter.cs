using System.Globalization;
using System.Windows.Data;

namespace WifiDirectTransfer.App;

/// <summary>
/// 将字节数格式化为 B / KB / MB / GB，供队列和详情面板显示。
/// </summary>
public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        if (value is null)
        {
            return "—";
        }

        var bytes = value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            _ => 0L
        };

        if (bytes < 0)
        {
            return "—";
        }

        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024L * 1024 => $"{bytes / 1024.0:0.0} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.0} MB",
            _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB"
        };
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}

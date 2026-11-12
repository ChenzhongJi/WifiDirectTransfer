using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace WifiDirectTransfer.App;

/// <summary>
/// 托盘图标与菜单。所有成员都在 UI 线程调用。
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Drawing.Icon _icon;
    private readonly bool _ownsIcon;
    private int _disposed;

    public TrayIconService()
    {
        _icon = LoadIcon(out _ownsIcon);

        var showItem = new Forms.ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => ShowRequested?.Invoke();

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(showItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = $"Wi-Fi Direct 文件传输 {BuildInfo.VersionText}",
            Visible = true,
            ContextMenuStrip = _menu
        };

        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke();
        _notifyIcon.BalloonTipClicked += (_, _) => BalloonClicked?.Invoke();
    }

    public event Action? ShowRequested;

    public event Action? ExitRequested;

    public event Action? BalloonClicked;

    public void ShowBalloon(string title, string message, bool isError = false)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = isError
                ? Forms.ToolTipIcon.Error
                : Forms.ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(5000);
        }
        catch (Exception)
        {
            // 通知失败不应影响主流程。
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();

            if (_ownsIcon)
            {
                _icon.Dispose();
            }
        }
        catch (Exception)
        {
            // 退出阶段忽略托盘资源释放异常。
        }
    }

    private static Drawing.Icon LoadIcon(out bool ownsIcon)
    {
        try
        {
            var executablePath = Environment.ProcessPath;

            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                var icon = Drawing.Icon.ExtractAssociatedIcon(executablePath);

                if (icon is not null)
                {
                    ownsIcon = true;
                    return icon;
                }
            }
        }
        catch (Exception)
        {
            // 回退到系统图标。
        }

        ownsIcon = false;
        return Drawing.SystemIcons.Application;
    }
}

using System.ComponentModel;
using System.Windows;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

public partial class UpdateWindow : Window
{
    private readonly UpdateManifest _manifest;
    private readonly Action? _onSkip;
    private CancellationTokenSource? _downloadCancellation;
    private string? _downloadedPath;
    private bool _installing;

    public UpdateWindow(
        UpdateManifest manifest,
        string currentVersion,
        Action? onSkip)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);

        _manifest = manifest;
        _onSkip = onSkip;

        InitializeComponent();

        TitleText.Text = $"发现新版本 {manifest.Version}";
        SubtitleText.Text = $"当前版本 {currentVersion}";
        BodyText.Text =
            $"更新包大小约 {FormatBytes(manifest.Size)}。" +
            "下载完成后会校验 SHA-256，然后关闭应用并静默安装。";

        if (manifest.NotesUri is null)
        {
            NotesPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void PrimaryButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_downloadedPath is not null)
        {
            InstallAndExit();
            return;
        }

        await DownloadAsync();
    }

    private async Task DownloadAsync()
    {
        if (_downloadCancellation is not null)
        {
            return;
        }

        _downloadCancellation = new CancellationTokenSource();
        PrimaryButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        DownloadProgressBar.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        StatusText.Text = "正在下载…";

        var progress = new Progress<UpdateDownloadProgress>(update =>
        {
            DownloadProgressBar.Value = update.Fraction;
            StatusText.Text =
                $"正在下载… {FormatBytes(update.Received)} / " +
                FormatBytes(update.Total);
        });

        try
        {
            var path = await UpdateService.DownloadAsync(
                _manifest,
                progress,
                _downloadCancellation.Token);

            _downloadedPath = path;
            DownloadProgressBar.Value = 1;
            StatusText.Text = "下载完成，SHA-256 校验通过。";
            PrimaryButton.Content = "安装并重启";
            PrimaryButton.IsEnabled = true;
            SkipButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            AppLog.Write($"更新包已下载并校验：{path}");
        }
        catch (OperationCanceledException)
        {
            Close();
        }
        catch (Exception exception)
        {
            AppLog.Write(
                $"更新下载失败：{exception.GetType().Name}: {exception.Message}");
            DownloadProgressBar.Visibility = Visibility.Collapsed;
            StatusText.Text = $"下载失败：{exception.Message}";
            PrimaryButton.IsEnabled = true;
            SkipButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
        }
        finally
        {
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;
        }
    }

    private void InstallAndExit()
    {
        if (_downloadedPath is null)
        {
            return;
        }

        var confirmed = MessageDialog.Confirm(
            this,
            "安装更新",
            "安装会关闭应用，并中断当前连接或正在进行的传输。是否继续？",
            defaultNo: false,
            MessageDialogKind.Warning);

        if (!confirmed)
        {
            return;
        }

        try
        {
            UpdateInstaller.Launch(_downloadedPath);
            _installing = true;
            AppLog.Write("已启动更新安装程序，应用即将退出。");

            if (Application.Current.MainWindow is MainWindow mainWindow)
            {
                mainWindow.AllowCloseForUpdate();
            }

            Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            AppLog.Write(
                $"启动更新安装失败：{exception.GetType().Name}: {exception.Message}");
            MessageDialog.ShowError(
                this,
                "安装更新",
                $"无法启动更新安装程序：{exception.Message}");
        }
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        _onSkip?.Invoke();
        Close();
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void NotesLink_Click(object sender, RoutedEventArgs e)
    {
        if (_manifest.NotesUri is not null)
        {
            UpdateCoordinator.OpenUrl(_manifest.NotesUri.ToString());
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_installing && _downloadCancellation is not null)
        {
            _downloadCancellation.Cancel();
        }

        base.OnClosing(e);
    }

    private static string FormatBytes(long bytes)
    {
        const double Kilobyte = 1024d;
        const double Megabyte = Kilobyte * 1024d;

        return bytes >= Megabyte
            ? $"{bytes / Megabyte:0.0} MB"
            : $"{bytes / Kilobyte:0} KB";
    }
}

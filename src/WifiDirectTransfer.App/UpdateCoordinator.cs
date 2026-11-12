using System.Diagnostics;
using System.Windows;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

/// <summary>
/// 串联更新检查和提示对话框。启动时静默检查，失败不打扰用户；
/// “关于”里的手动检查会显示明确结果。
/// </summary>
internal static class UpdateCoordinator
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task CheckAsync(Window? owner, bool manual)
    {
        // 同一时间只允许一个检查，避免启动检查与手动检查叠加。
        if (!await Gate.WaitAsync(0).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await CheckCoreAsync(owner, manual).ConfigureAwait(true);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task CheckCoreAsync(Window? owner, bool manual)
    {
        var preferences = new ReceiverPreferencesStore().Load();

        if (!InstallationInfo.IsInstalledCopy)
        {
            if (manual)
            {
                ShowPortableNotice(owner);
            }

            return;
        }

        UpdateManifest? manifest;

        try
        {
            manifest = await UpdateService
                .CheckAsync(CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppLog.Write($"检查更新失败：{exception.GetType().Name}: {exception.Message}");

            if (manual)
            {
                MessageDialog.ShowError(
                    owner,
                    "检查更新",
                    $"检查更新失败：{exception.Message}");
            }

            return;
        }

        if (manifest is null)
        {
            AppLog.Write("检查更新：没有可用的更新信息。");

            if (manual)
            {
                MessageDialog.ShowInfo(
                    owner,
                    "检查更新",
                    "没有找到可用的更新信息，请稍后再试。");
            }

            return;
        }

        if (manifest.Version <= UpdateService.CurrentVersion)
        {
            AppLog.Write(
                $"检查更新：当前已是最新版本（{UpdateService.CurrentVersionText}）。");

            if (manual)
            {
                MessageDialog.ShowInfo(
                    owner,
                    "检查更新",
                    $"当前已是最新版本（{UpdateService.CurrentVersionText}）。");
            }

            return;
        }

        if (!manual &&
            string.Equals(
                preferences.SkippedUpdateVersion,
                manifest.Version.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AppLog.Write(
            $"发现新版本 {manifest.Version}，" +
            $"当前 {UpdateService.CurrentVersionText}。");

        var window = new UpdateWindow(
            manifest,
            UpdateService.CurrentVersionText,
            onSkip: () => SaveSkippedVersion(manifest.Version.ToString()))
        {
            Owner = owner
        };

        if (owner is null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ShowDialog();
    }

    private static void ShowPortableNotice(Window? owner)
    {
        var open = MessageDialog.Confirm(
            owner,
            "检查更新",
            "便携版不支持自动更新。是否打开 GitHub Releases 页面下载新版本？",
            defaultNo: false,
            MessageDialogKind.Information);

        if (open)
        {
            OpenUrl(UpdateService.ReleasesPageUrl);
        }
    }

    private static void SaveSkippedVersion(string version)
    {
        var store = new ReceiverPreferencesStore();
        var preferences = store.Load();
        preferences.SkippedUpdateVersion = version;
        store.Save(preferences);
        AppLog.Write($"已跳过版本 {version}。");
    }

    internal static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            AppLog.Write($"打开链接失败：{exception.Message}");
        }
    }
}

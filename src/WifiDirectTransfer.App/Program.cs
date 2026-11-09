using System.Runtime.InteropServices;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

internal static class Program
{
    private const string AppUserModelId = "WifiDirectTransfer.M0";
    private const string SingleInstanceMutexName =
        @"Local\WifiDirectTransfer.SingleInstance";
    private const string ActivationEventName =
        @"Local\WifiDirectTransfer.Activate";

    [STAThread]
    private static void Main()
    {
        StartupLog.Write("Program.Main executed.");

        // 托盘应用必须单实例，否则两个进程会同时抢占 PeerFinder 和托盘图标。
        using var instanceMutex = new Mutex(
            initiallyOwned: true,
            SingleInstanceMutexName,
            out var isFirstInstance);

        if (!isFirstInstance)
        {
            StartupLog.Write("检测到已有实例，尝试激活现有窗口后退出。");
            SignalExistingInstance();
            return;
        }

        var hresult = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        if (hresult != 0)
        {
            throw new InvalidOperationException(
                "无法初始化应用的 Wi-Fi Direct 标识，设备发现功能不可用。请重启应用后重试。")
            {
                HResult = hresult
            };
        }

        var app = new App();
        app.InitializeComponent();

        ThemeManager.Initialize(
            new ReceiverPreferencesStore().Load().ThemeMode);

        using var activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);

        var activationThread = new Thread(
            () => ListenForActivation(app, activationEvent))
        {
            IsBackground = true,
            Name = "SingleInstanceActivation"
        };
        activationThread.Start();

        app.Run();
    }

    private static void ListenForActivation(App app, EventWaitHandle activationEvent)
    {
        while (true)
        {
            try
            {
                if (!activationEvent.WaitOne())
                {
                    return;
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                app.Dispatcher.Invoke(() =>
                {
                    if (app.MainWindow is MainWindow mainWindow)
                    {
                        mainWindow.ShowFromTray();
                    }
                });
            }
            catch (Exception)
            {
                // 应用正在退出或 Dispatcher 已关闭。
            }
        }
    }

    private static void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var activationEvent =
                    EventWaitHandle.OpenExisting(ActivationEventName);
                activationEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 现有实例可能仍在启动，稍等后重试。
                Thread.Sleep(100);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);
}

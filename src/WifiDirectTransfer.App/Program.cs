using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

internal static class Program
{
    private const string AppUserModelId = "WifiDirectTransfer.M0";
    private const string SingleInstanceMutexName =
        @"Local\WifiDirectTransfer.SingleInstance";
    private const string ActivationEventName =
        @"Local\WifiDirectTransfer.Activate";
    private const string ActivationPipeName =
        "WifiDirectTransfer.Activation";

    private static readonly object PendingPathsGate = new();
    private static readonly List<string> PendingPaths = new();

    [STAThread]
    private static void Main(string[] args)
    {
        StartupLog.Write("Program.Main executed.");

        var launchPaths = LaunchArguments.ParsePaths(args);
        var startHidden = args.Any(argument =>
            string.Equals(argument, "--tray", StringComparison.OrdinalIgnoreCase));

        if (args.Length > 0)
        {
            StartupLog.Write(
                $"命令行参数：{string.Join(" | ", args)}；" +
                $"解析到 {launchPaths.Count} 个路径。");
        }

        // 托盘应用必须单实例，否则两个进程会同时抢占 PeerFinder 和托盘图标。
        using var instanceMutex = new Mutex(
            initiallyOwned: true,
            SingleInstanceMutexName,
            out var isFirstInstance);

        if (!isFirstInstance)
        {
            StartupLog.Write("检测到已有实例，尝试传递路径并激活现有窗口。");
            SignalExistingInstance(launchPaths);
            return;
        }

        if (launchPaths.Count > 0)
        {
            AddPendingPaths(launchPaths);
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

        var pipeThread = new Thread(
            () => ListenForPipeActivations(app))
        {
            IsBackground = true,
            Name = "SingleInstancePathActivation"
        };
        pipeThread.Start();

        if (startHidden)
        {
            MainWindow.RequestStartHidden();
            StartupLog.Write("开机自启：以托盘模式启动，不显示主窗口。");
        }

        app.Run();
    }

    /// <summary>
    /// 主窗口加载后取走启动参数里的路径。窗口尚未创建时由管道监听线程暂存。
    /// </summary>
    internal static IReadOnlyList<string> TakePendingPaths()
    {
        lock (PendingPathsGate)
        {
            if (PendingPaths.Count == 0)
            {
                return Array.Empty<string>();
            }

            var paths = PendingPaths.ToArray();
            PendingPaths.Clear();
            return paths;
        }
    }

    private static void AddPendingPaths(IEnumerable<string> paths)
    {
        lock (PendingPathsGate)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (!PendingPaths.Contains(
                        path,
                        StringComparer.OrdinalIgnoreCase))
                {
                    PendingPaths.Add(path);
                }
            }
        }
    }

    private static void ListenForPipeActivations(App app)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    ActivationPipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.CurrentUserOnly);

                server.WaitForConnection();

                using var reader = new StreamReader(
                    server,
                    new UTF8Encoding(false),
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096,
                    leaveOpen: false);

                var paths = new List<string>();

                while (reader.ReadLine() is { } line)
                {
                    var path = line.Trim();

                    if (path.Length > 0)
                    {
                        paths.Add(path);
                    }
                }

                DispatchActivation(app, paths);
            }
            catch (Exception exception)
            {
                StartupLog.Write(
                    "读取单实例路径失败：" +
                    exception.GetType().Name);

                try
                {
                    Thread.Sleep(100);
                }
                catch (Exception)
                {
                    return;
                }
            }
        }
    }

    private static void DispatchActivation(
        App app,
        IReadOnlyList<string> paths)
    {
        try
        {
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (app.MainWindow is MainWindow mainWindow)
                {
                    mainWindow.ActivateFromExternalLaunch(paths);
                }
                else if (paths.Count > 0)
                {
                    AddPendingPaths(paths);
                }
            }));
        }
        catch (Exception)
        {
            if (paths.Count > 0)
            {
                AddPendingPaths(paths);
            }
        }
    }

    private static void ListenForActivation(
        App app,
        EventWaitHandle activationEvent)
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

    private static void SignalExistingInstance(
        IReadOnlyList<string> paths)
    {
        if (SendPathsToExistingInstance(paths))
        {
            return;
        }

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

    private static bool SendPathsToExistingInstance(
        IReadOnlyList<string> paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                ActivationPipeName,
                PipeDirection.Out,
                PipeOptions.CurrentUserOnly);

            client.Connect(3000);

            using var writer = new StreamWriter(
                client,
                new UTF8Encoding(false))
            {
                AutoFlush = true
            };

            foreach (var path in paths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    writer.WriteLine(path);
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);
}

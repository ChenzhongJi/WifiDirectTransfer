using System.Runtime.InteropServices;
using Windows.Networking.Proximity;

namespace WifiDirectTransfer.PeerFinderProbe;

/// <summary>
/// PeerFinder 最小探测工具：只调用 Windows.Networking.Proximity，不依赖本项目的连接/传输代码。
/// 用于判断“发现不到设备”是 PeerFinder/Windows 环境问题，还是应用代码问题。
/// </summary>
internal static class Program
{
    private const string DefaultAumid = "WifiDirectTransfer.M0";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr hWnd,
        out uint processId);

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var mode = GetOption(args, "--mode") ?? "scan";
        var seconds = int.TryParse(GetOption(args, "--seconds"), out var parsed)
            ? Math.Clamp(parsed, 5, 3600)
            : 120;
        var aumid = GetOption(args, "--aumid") ?? DefaultAumid;

        if (mode is not ("scan" or "wait" or "watcher"))
        {
            Console.WriteLine("--mode 支持 scan、wait、watcher。");
            return 1;
        }

        Console.Title =
            $"PeerFinder 探测（{mode}）— {Environment.MachineName}";

        var hresult = SetCurrentProcessExplicitAppUserModelID(aumid);

        if (hresult != 0)
        {
            Console.WriteLine(
                $"SetCurrentProcessExplicitAppUserModelID 失败：0x{hresult:X8}");
            return 1;
        }

        Console.WriteLine($"机器：{Environment.MachineName}");
        Console.WriteLine($"AppUserModelID：{aumid}");
        Console.WriteLine($"系统：{Environment.OSVersion.VersionString}");
        Console.WriteLine($"模式：{mode}，持续 {seconds} 秒");
        Console.WriteLine(
            "提示：等待端请保持本控制台窗口在前台；另一端运行 scan 模式。");

        if (args.Contains("--pause", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine(
                "请点击此窗口使其成为前台窗口，然后按 Enter 开始探测…");
            Console.ReadLine();
        }

        PrintForegroundWindow();
        Console.WriteLine();

        PeerFinder.DisplayName = $"WDT-PROBE-{Environment.MachineName}";
        PeerFinder.AllowWiFiDirect = true;
        PeerFinder.AllowBluetooth = false;
        PeerFinder.AllowInfrastructure = false;

        PeerDiscoveryTypes discoveryTypes;

        try
        {
            discoveryTypes = PeerFinder.SupportedDiscoveryTypes;
        }
        catch (Exception exception)
        {
            PrintException("读取 SupportedDiscoveryTypes", exception);
            return 2;
        }

        Console.WriteLine($"[1/4] SupportedDiscoveryTypes：{discoveryTypes}");
        Console.WriteLine(
            $"AllowWiFiDirect={PeerFinder.AllowWiFiDirect}, " +
            $"AllowInfrastructure={PeerFinder.AllowInfrastructure}, " +
            $"AllowBluetooth={PeerFinder.AllowBluetooth}");
        Console.WriteLine($"DisplayName：{PeerFinder.DisplayName}");

        if ((discoveryTypes & PeerDiscoveryTypes.Browse) !=
            PeerDiscoveryTypes.Browse)
        {
            Console.WriteLine(
                "当前系统/网卡不支持 Wi-Fi Direct Browse 发现，探测结束。");
            return 2;
        }

        PeerFinder.ConnectionRequested += (_, e) =>
            Console.WriteLine(
                $"[ConnectionRequested] " +
                $"{e.PeerInformation.DisplayName} / {e.PeerInformation.Id}");

        try
        {
            PeerFinder.Start();
            Console.WriteLine("[2/4] PeerFinder.Start 完成。");
        }
        catch (Exception exception)
        {
            PrintException("PeerFinder.Start", exception);
            return 4;
        }

        PeerWatcher? watcher = null;
        var found = false;

        try
        {
            if (mode is "watcher" or "wait")
            {
                watcher = PeerFinder.CreateWatcher();
                watcher.Added += (_, peer) =>
                {
                    found = true;
                    Console.WriteLine(
                        $"[Watcher.Added] {peer.DisplayName} / {peer.Id}");
                };
                watcher.Removed += (_, peer) =>
                    Console.WriteLine(
                        $"[Watcher.Removed] {peer.DisplayName} / {peer.Id}");
                watcher.EnumerationCompleted += (_, _) =>
                    Console.WriteLine("[Watcher] 初始枚举完成。");
                watcher.Stopped += (_, _) =>
                    Console.WriteLine(
                        $"[Watcher] 已停止（Status={watcher.Status}）。");
                watcher.Start();
                Console.WriteLine(
                    $"[3/4] PeerWatcher 已启动（Status={watcher.Status}）。");
                Console.WriteLine(
                    "[4/4] 等待 PeerWatcher.Added 事件；保持本窗口在前台，另一端运行 scan。");
            }
        }
        catch (Exception exception)
        {
            PrintException("创建/启动 PeerWatcher", exception);
        }

        if (watcher is null)
        {
            Console.WriteLine(
                "[4/4] 进入 FindAllPeersAsync 循环；保持本窗口在前台。");
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        var startedAt = DateTimeOffset.UtcNow;
        var nextHeartbeat = startedAt.AddSeconds(5);
        var attempt = 0;
        string? lastWatcherStatus = null;

        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (watcher is not null)
                {
                    var status = watcher.Status.ToString();

                    if (status != lastWatcherStatus)
                    {
                        lastWatcherStatus = status;
                        Console.WriteLine(
                            $"[{DateTime.Now:HH:mm:ss}] Watcher.Status={status}");
                    }
                }
                else
                {
                    attempt++;
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] 第 {attempt} 次 FindAllPeersAsync…");

                    try
                    {
                        var peers = await PeerFinder.FindAllPeersAsync();
                        Console.WriteLine($"  返回 {peers.Count} 台设备。");

                        foreach (var peer in peers)
                        {
                            found = true;
                            Console.WriteLine(
                                $"  peer: DisplayName={peer.DisplayName}, " +
                                $"Id={peer.Id}, ServiceName={peer.ServiceName}");
                        }
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine(
                            $"  FindAllPeersAsync 异常：{exception.GetType().Name}: " +
                            $"{exception.Message} (0x{exception.HResult:X8})");
                    }
                }

                if (found && mode == "scan")
                {
                    break;
                }

                if (DateTimeOffset.UtcNow >= nextHeartbeat)
                {
                    nextHeartbeat = nextHeartbeat.AddSeconds(5);
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] 等待中… " +
                        $"已运行 {(int)(DateTimeOffset.UtcNow - startedAt).TotalSeconds} 秒，" +
                        $"Status={watcher?.Status.ToString() ?? "scan"}");
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
        finally
        {
            try
            {
                watcher?.Stop();
            }
            catch (Exception)
            {
                // 清理失败不影响退出。
            }

            PeerFinder.Stop();
            Console.WriteLine("PeerFinder 已停止。");
        }

        Console.WriteLine(
            found
                ? "探测结果：发现对端。"
                : $"探测结果：未发现任何对端（Status={watcher?.Status.ToString() ?? "scan"}）。");
        return found ? 0 : 3;
    }

    private static void PrintForegroundWindow()
    {
        var window = GetForegroundWindow();

        if (window == IntPtr.Zero)
        {
            Console.WriteLine("前台窗口：无法读取。");
            return;
        }

        _ = GetWindowThreadProcessId(window, out var processId);

        Console.WriteLine(
            $"前台窗口进程 PID={processId}，本探测进程 PID={Environment.ProcessId}" +
            (processId == Environment.ProcessId
                ? "（前台正确）"
                : "（如果当前是 Windows Terminal，父终端在前台也属于正常）"));
    }

    private static void PrintException(string step, Exception exception)
    {
        Console.WriteLine(
            $"!!! {step} 失败：{exception.GetType().Name}: " +
            $"{exception.Message} (0x{exception.HResult:X8})");
        Console.WriteLine($"    HRESULT 含义：{DescribeHResult(exception.HResult)}");
    }

    private static string DescribeHResult(int hresult) => hresult switch
    {
        unchecked((int)0x80070017) =>
            "ERROR_CRC / 数据无效。常见于 Wi-Fi Direct 服务或虚拟网卡状态异常",
        unchecked((int)0x80070005) => "E_ACCESSDENIED / 权限不足",
        unchecked((int)0x80070490) => "ERROR_NOT_FOUND / 找不到元素",
        unchecked((int)0x80070057) => "E_INVALIDARG / 参数无效",
        unchecked((int)0x8007048F) => "设备未就绪",
        _ => "未知 HRESULT，请记录该值"
    };

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return index + 1 < args.Length ? args[index + 1] : null;
        }

        return null;
    }
}

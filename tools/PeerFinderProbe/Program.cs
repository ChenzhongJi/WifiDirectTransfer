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
        Console.WriteLine();

        PeerFinder.DisplayName = $"WDT-PROBE-{Environment.MachineName}";
        PeerFinder.AllowWiFiDirect = true;
        PeerFinder.AllowBluetooth = false;
        PeerFinder.AllowInfrastructure = false;

        var discoveryTypes = PeerFinder.SupportedDiscoveryTypes;
        Console.WriteLine($"SupportedDiscoveryTypes：{discoveryTypes}");
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

        PeerFinder.Start();
        Console.WriteLine("PeerFinder.Start 完成。");

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
                    $"PeerWatcher 已启动（Status={watcher.Status}）。");
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"PeerWatcher 启动失败：{exception.GetType().Name}: " +
                $"{exception.Message}");
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
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
                : "探测结果：未发现任何对端。");
        return found ? 0 : 3;
    }

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

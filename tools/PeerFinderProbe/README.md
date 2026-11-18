# PeerFinder 发现探测工具

用于区分“发现不到设备”是 **Windows/PeerFinder 环境问题**，还是 **WifiDirectTransfer 应用问题**。

工具只使用 `Windows.Networking.Proximity.PeerFinder`，不依赖本项目的连接、协议或 UI 代码。

## 构建

```powershell
dotnet build tools/PeerFinderProbe/PeerFinderProbe.csproj -c Release
```

生成目录：

```text
tools/PeerFinderProbe/bin/Release/net8.0-windows10.0.19041.0/
```

## 两台机器测试

**机器 A（模拟接收端）**：

```powershell
.\PeerFinderProbe.exe --mode watcher --seconds 180
```

**机器 B（模拟发送端）**：

```powershell
.\PeerFinderProbe.exe --mode scan --seconds 60
```

要求：

- 两端使用相同 `--aumid`（默认 `WifiDirectTransfer.M0`，与正式应用一致）。
- 两个控制台窗口都必须保持**前台**；PeerFinder 只广播前台应用。
- 两台机器同一局域网/无线范围内，Wi-Fi 打开。

预期：B 的 `FindAllPeersAsync` 输出 `peer: DisplayName=WDT-PROBE-A`。

也可以让探测工具与正式应用交叉验证：

- A 运行 `PeerFinderProbe --mode watcher`；
- B 打开 WifiDirectTransfer，选择“连接设备”并扫描；
- 正式应用的列表里应出现 `WDT-PROBE-A` 设备。

## 结果解读

| 结果 | 含义 |
|---|---|
| Probe B 找到 `WDT-PROBE-A` | PeerFinder 栈正常，问题在正式应用的发现/角色/身份逻辑 |
| Probe B 也返回 0 台，且看得到 A 的 `PeerWatcher` 初始枚举完成 | Windows/PeerFinder 环境问题（系统、服务、前台、AUMID 或网卡 P2P） |
| `Watcher.Status=Aborted` | PeerWatcher 被系统中止；确认控制台窗口在前台后重试，并检查 Wi-Fi Direct 服务/系统事件日志 |
| `Watcher.Status=EnumerationCompleted` 后 `Stopped` | 正常收尾；若没有 `Added` 事件，说明当时没有可发现的对端 |

## 补充检查

```powershell
Get-Service WFDSConMgrSvc
netsh wlan show wirelesscapabilities
Get-NetAdapter -Name WLAN | Select-Object DriverVersion,DriverDate
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Netwtw10'; StartTime=(Get-Date).AddDays(-3)}
```

如果 `WFDSConMgrSvc` 被停止或禁用，尝试启动它；如果 `Wi-Fi Direct Device / GO / Client / P2P Device Discovery` 不是 Supported，说明系统/网卡层不支持。

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
.\PeerFinderProbe.exe --mode watcher --seconds 180 --pause
```

**机器 B（模拟发送端）**：

```powershell
.\PeerFinderProbe.exe --mode scan --seconds 60 --pause
```

如果 B 的 `scan` 报 `FindAllPeersAsync 异常：COMException (0x80070017)`，用下面这个模式验证应用新增的兜底路径：

```powershell
.\PeerFinderProbe.exe --mode scan-fallback --seconds 60 --pause
```

`scan-fallback` 会先调用 `FindAllPeersAsync`，遇到 `0x80070017` 时自动改用 `PeerWatcher` 等待 5 秒，与应用当前的兜底逻辑一致。

`--pause` 会要求你先点击控制台窗口、按 Enter 后再启动，确保应用处于前台。

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

## 做反向与 2.4GHz 对照

- **反向**：B 运行 watcher，A 运行 scan。若反向能找到，说明问题在单向的网卡/接收状态，而不是应用身份。
- **2.4GHz**：两台电脑都连接 2.4GHz 的 Wi-Fi 热点再测；部分网卡在“5GHz 基础设施连接 + 2.4GHz P2P 发现”并发时无法工作。
- **断开 AP**：也可以两台都断开 Wi-Fi 关联（保持 Wi-Fi 开关打开），只靠 Wi-Fi Direct 发现，再测。


## 补充检查

```powershell
Get-Service WFDSConMgrSvc
netsh wlan show wirelesscapabilities
Get-NetAdapter -Name WLAN | Select-Object DriverVersion,DriverDate
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Netwtw10'; StartTime=(Get-Date).AddDays(-3)}
```

如果 `WFDSConMgrSvc` 被停止或禁用，尝试启动它；如果 `Wi-Fi Direct Device / GO / Client / P2P Device Discovery` 不是 Supported，说明系统/网卡层不支持。

## 0x80070017（ERROR_CRC）

如果探测在 `[2/4] PeerFinder.Start` 打印 `COMException (0x80070017)`，表示 Wi-Fi Direct 服务/虚拟网卡没有正常启动，通常不是应用代码问题。在故障机器上依次尝试：

1. 设备管理器 → 网络适配器：启用（必要时卸载后扫描硬件改动）
   `Microsoft Wi-Fi Direct Virtual Adapter` 和 `Microsoft Wi-Fi Direct Virtual Adapter #2`。
2. 管理员 PowerShell：
   ```powershell
   Restart-Service WlanSvc
   Get-Service WFDSConMgrSvc | Start-Service
   ```
   （重启 WlanSvc 会让 Wi-Fi 短暂断开。）
3. 关闭再打开 Wi-Fi，或完整重启电脑（`shutdown /s /t 0`，避免快速启动）。
4. 检查网卡/系统日志：
   ```powershell
   Get-PnpDevice -Class Net | Where-Object Status -ne 'OK'
   Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=(Get-Date).AddHours(-1)}
   ```
5. 如仍失败，运行 `sfc /scannow` 和
   `DISM /Online /Cleanup-Image /RestoreHealth`，并考虑回退最近一次 Windows 更新。


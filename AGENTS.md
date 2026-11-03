# AGENTS.md

本文件记录 WifiDirectTransfer 的项目约定，所有开发者和 AI 代理在修改代码前都必须遵循。需求编号与描述的事实来源见 `docs/REQUIREMENTS.md`。

## 硬性技术约束

1. M0 连接栈采用 PeerFinder（`Windows.Networking.Proximity`）+ StreamSocket。不使用 `Windows.Devices.WiFiDirect`，不使用 `System.Net.Sockets`。方案 B（WiFiDirectDevice + TcpListener）仅作为 Step 0 失败后的备选，不在默认路径中。

2. 桌面应用调用 PeerFinder 之前，必须在自定义 `Program.Main` 中、创建 `Application` 和任何窗口之前调用 `SetCurrentProcessExplicitAppUserModelID`，并检查返回值。不调用这个 API，PeerFinder 的发现功能完全无法工作。P/Invoke 签名：

   ```csharp
   [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
   private static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);
   ```

   传入 `"WifiDirectTransfer.M0"`。两端必须使用同一构建、同一 AppUserModelID，否则 `FindAllPeersAsync` 可能找不到对方。

3. 调用 `PeerFinder.FindAllPeersAsync` 之前，必须先检查 `PeerFinder.SupportedDiscoveryTypes` 是否包含 `PeerDiscoveryTypes.Browse`。不包含则直接返回“当前无线网卡或驱动不支持 Wi-Fi Direct 发现”，不继续调用发现 API。

4. 不使用 `Windows.Devices.WiFiDirect`、`Windows.Devices.WiFiDirect.Services`、`System.Net.Sockets`。文件传输走 `PeerFinder.ConnectAsync` 返回的 `StreamSocket`，不自己开 `TcpListener`，不固定端口。

5. `StreamSocket.InputStream` 使用 `AsStream(InputStreamOptions.Partial)`，读取定长头部时使用 `ReadExactlyAsync`。

6. 所有 WinRT 异常必须映射为用户可读的中文提示，集中放在 `WifiDirectErrorMapper` 类中，UI 层不得直接显示 HRESULT。不要根据本地化的 `Exception.Message` 判断错误。

## 架构约定

- 项目分为四个项目：
  - App：WPF 桌面应用。
  - Core：`net8.0`，无 WinRT，可单元测试。
  - Windows：`net8.0-windows10.0.19041.0`，承载 PeerFinder 实现。
  - Tests：测试项目。
- Core 项目包含协议编解码、文件发送/接收、哈希、错误契约、`IDuplexChannel` 抽象。不引用 WinRT。
- Windows 项目实现 `WifiDirectConnectionService`、`IDuplexChannel`（StreamSocket 包装）、`WifiDirectErrorMapper`。
- 传输层只依赖 `IDuplexChannel` 的 Input/Output Stream，不依赖具体连接栈。
- 文件传输协议：24 字节定长前缀（Magic `"WFD1"`、Version、MessageType、HeaderLength、FileNameLength、FileSize、ChecksumAlgorithm、Flags、Reserved）+ 变长 UTF-8 文件名 + 文件体 + 32 字节 SHA-256 尾部。
- 块大小 64 KiB。M0 无 ACK、无每块头、无断点续传。M1 升级为多路复用帧。
- 接收确认对话框是必选安全步骤，15 秒超时自动拒绝。它不是 ACK。
- 敏感功能（剪贴板同步、远程文本输入）默认关闭，首次使用时询问用户。

## 需求事实来源

需求编号、范围与验收基线统一记录在 `docs/REQUIREMENTS.md`，不得在实现中自行增删或改写编号含义。

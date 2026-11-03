## 架构决策

- 连接栈：PeerFinder（Windows.Networking.Proximity）+ StreamSocket。
- 不使用 Windows.Devices.WiFiDirect 和 System.Net.Sockets。
- 两端使用同一 AppUserModelID（WifiDirectTransfer.M0），由自定义 Program.Main 在任何窗口创建前调用 SetCurrentProcessExplicitAppUserModelID。
- 协议头：24 字节定长前缀 + 变长 UTF-8 文件名 + 文件体 + 32 字节 SHA-256 尾部。
- Magic "WFD1"，Version 1，HeaderLength = 24 + FileNameLength，FileNameLength 1..255。
- 块大小 64 KiB。M0 无 ACK、无每块头、无断点续传。
- 接收端必须保留确认对话框，15 秒超时自动拒绝。

## Step 0 Go/No-Go 验证（30 分钟）

1. 两端同构建同 AppUserModelID，保持前台。
2. 两端 PeerFinder.Start() 无异常。
3. 发送端 FindAllPeersAsync 能列出接收端。
4. 发送端 ConnectAsync 后，接收端 ConnectionRequested 被触发。
5. 接收端 ConnectAsync(e.PeerInformation) 后，双方各拿到 StreamSocket，IsConnected=true。
6. 双方各发 1 字节并收到。
7. 日志中记录 StreamSocket.Information 的 LocalHostName/LocalPort/RemoteHostName/RemotePort。
8. 观察防火墙是否弹窗。弹窗则说明“不需要防火墙规则”的假设不成立，需记录。

任一步失败 → 暂停 M0，切方案 B，不要在此机器上继续调试 PeerFinder 协议。

## Step 5 手工验收

| 用例 | 预期 |
|---|---|
| 1 MiB 正常传输 | 接收端 SHA-256 与发送端一致 |
| 发送端 512 KiB 时杀掉进程 | 接收端报传输截断，.part 删除 |
| 篡改数据/摘要 | 接收端报校验失败，.part 删除 |
| 传输中断开 Wi-Fi | 接收端在超时内报连接断开 |
| 接收端拒绝连接 | 发送端报“对方拒绝或未在时限内接受” |
| 无接收端时搜索 | 10 秒内报搜索超时，不崩溃 |
| 不支持 Wi-Fi Direct 的机器 | 预检直接提示，不调用 FindAllPeersAsync |

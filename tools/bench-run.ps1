# 诊断用启动器：带 WDT_* 环境变量启动已发布的 exe。
# 仅用于现场 A/B 测量，不修改任何系统设置或持久化配置。
#
# 用法：
#   .\tools\bench-run.ps1 -Window 32 -Ack 8
#   .\tools\bench-run.ps1 -Window 16 -Ack 1
#   .\tools\bench-run.ps1 -Window 64 -Ack 64 -SocketBufferKb 4096

param(
    [ValidateRange(1, 64)]
    [int]$Window = 32,

    [ValidateRange(1, 64)]
    [int]$Ack = 8,

    [ValidateRange(0, 16384)]
    [int]$SocketBufferKb = 0,

    [string]$AppPath = ''
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($AppPath)) {
    $AppPath = Join-Path $repositoryRoot 'publish\WifiDirectTransfer-win-x64\WifiDirectTransfer.App.exe'
}

if (-not (Test-Path -LiteralPath $AppPath)) {
    throw "找不到 $AppPath。请先运行 publish.ps1。"
}

$env:WDT_DIAG = '1'
$env:WDT_WINDOW_BLOCKS = "$Window"
$env:WDT_ACK_BLOCKS = "$Ack"
$env:WDT_SOCKET_BUFFER_KB = "$SocketBufferKb"

$logPath = Join-Path $env:LOCALAPPDATA 'WifiDirectTransfer\logs\app.log'

$running = Get-Process WifiDirectTransfer.App -ErrorAction SilentlyContinue

if ($running) {
    Write-Warning '检测到 WifiDirectTransfer.App 正在运行。'
    Write-Warning '请先退出应用（不要强制结束传输中的进程），再重新执行本脚本。'
    exit 1
}

Write-Host '=== 诊断调参 ==='
Write-Host "window=$Window, ackEvery=$Ack, socketBufferKb=$SocketBufferKb"
Write-Host "日志：$logPath"
Write-Host ''
Write-Host '两端都要使用同一个预览包；窗口和 ACK 间隔由发送端决定。'
Write-Host '请在应用中传输 200 MB 文件，结束后从日志复制以下两行：'
Write-Host '  窗口传输统计：window=..., ackEvery=..., chunks=..., ackRttMs=..., totalMs=...'
Write-Host '  窗口接收统计：chunks=..., acks=..., readMs=..., writeMs=..., ackMs=...'
Write-Host ''

& $AppPath

# Diagnostic launcher: starts the published exe with WDT_* environment variables.
# For on-site A/B measurements only. Does not change system settings or persisted config.
#
# Usage:
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
    throw "Cannot find $AppPath. Run publish.ps1 first."
}

$env:WDT_DIAG = '1'
$env:WDT_WINDOW_BLOCKS = "$Window"
$env:WDT_ACK_BLOCKS = "$Ack"
$env:WDT_SOCKET_BUFFER_KB = "$SocketBufferKb"

$logPath = Join-Path $env:LOCALAPPDATA 'WifiDirectTransfer\logs\app.log'

$running = Get-Process WifiDirectTransfer.App -ErrorAction SilentlyContinue

if ($running) {
    Write-Warning 'WifiDirectTransfer.App is running.'
    Write-Warning 'Close the app first (do not kill a transfer in progress), then run this script again.'
    exit 1
}

Write-Host '=== Diagnostic tuning ==='
Write-Host "window=$Window, ackEvery=$Ack, socketBufferKb=$SocketBufferKb"
Write-Host "log: $logPath"
Write-Host ''
Write-Host 'Use the same preview build on both machines. The sender decides window and ACK interval.'
Write-Host 'Transfer a 200 MB file, then copy these two lines from the log:'
Write-Host '  send stats: window=..., ackEvery=..., chunks=..., ackRttMs=..., totalMs=...'
Write-Host '  recv stats: chunks=..., acks=..., readMs=..., writeMs=..., ackMs=...'
Write-Host ''

& $AppPath

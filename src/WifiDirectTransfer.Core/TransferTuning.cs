namespace WifiDirectTransfer.Core;

/// <summary>
/// 传输参数：默认值取自 <see cref="FileTransferProtocol"/>，
/// 仅在设置 <c>WDT_DIAG=1</c> 时才允许用环境变量覆盖，用于现场 A/B 测量。
/// 覆盖值一律夹紧到协议允许范围，正式用户不受影响。
/// </summary>
public static class TransferTuning
{
    public const string DiagnosticVariable = "WDT_DIAG";
    public const string WindowBlocksVariable = "WDT_WINDOW_BLOCKS";
    public const string AckBlocksVariable = "WDT_ACK_BLOCKS";
    public const string SocketBufferVariable = "WDT_SOCKET_BUFFER_KB";

    /// <summary>诊断模式下 socket 缓冲的上限，避免误设耗尽内核内存。</summary>
    public const int MaxSocketBufferBytes = 16 * 1024 * 1024;

    static TransferTuning()
    {
        IsDiagnostic = IsEnabled(
            Environment.GetEnvironmentVariable(DiagnosticVariable));

        if (!IsDiagnostic)
        {
            RequestedWindowBlocks = FileTransferProtocol.DefaultWindowSize;
            RequestedAckBlocks = FileTransferProtocol.DefaultWindowAckInterval;
            RequestedSocketBufferBytes = 0;
            WindowBlocks = RequestedWindowBlocks;
            AckBlocks = RequestedAckBlocks;
            SocketBufferBytes = 0;
            return;
        }

        RequestedWindowBlocks = ReadInteger(WindowBlocksVariable)
            ?? FileTransferProtocol.DefaultWindowSize;
        WindowBlocks = ClampWindowBlocks(RequestedWindowBlocks);

        RequestedAckBlocks = ReadInteger(AckBlocksVariable)
            ?? FileTransferProtocol.DefaultWindowAckInterval;
        AckBlocks = ClampAckBlocks(RequestedAckBlocks, WindowBlocks);

        var requestedBufferKb = ReadInteger(SocketBufferVariable) ?? 0;
        RequestedSocketBufferBytes = requestedBufferKb;
        SocketBufferBytes = ClampSocketBufferBytes(
            (long)requestedBufferKb * 1024);
    }

    /// <summary>是否处于诊断调参模式。</summary>
    public static bool IsDiagnostic { get; }

    /// <summary>发送端窗口块数，1..<see cref="FileTransferProtocol.MaxWindowSize"/>。</summary>
    public static int WindowBlocks { get; }

    /// <summary>累计 ACK 间隔块数，1..<see cref="WindowBlocks"/>。</summary>
    public static int AckBlocks { get; }

    /// <summary>socket 发送缓冲字节数，0 表示沿用系统默认值。</summary>
    public static int SocketBufferBytes { get; }

    public static int RequestedWindowBlocks { get; }

    public static int RequestedAckBlocks { get; }

    public static int RequestedSocketBufferBytes { get; }

    /// <summary>实际生效的参数，用于连接和传输日志。</summary>
    public static string Describe() =>
        $"window={WindowBlocks}, ackEvery={AckBlocks}, " +
        $"socketBuffer={SocketBufferBytes}";

    /// <summary>
    /// 请求值被夹紧时的说明；没有夹紧或未启用诊断时返回 <c>null</c>。
    /// </summary>
    public static string? DescribeClampedRequest()
    {
        if (!IsDiagnostic)
        {
            return null;
        }

        var parts = new List<string>(3);

        if (RequestedWindowBlocks != WindowBlocks)
        {
            parts.Add(
                $"{WindowBlocksVariable}={RequestedWindowBlocks}->{WindowBlocks}");
        }

        if (RequestedAckBlocks != AckBlocks)
        {
            parts.Add(
                $"{AckBlocksVariable}={RequestedAckBlocks}->{AckBlocks}");
        }

        var requestedBufferKb = RequestedSocketBufferBytes;
        var effectiveBufferKb = SocketBufferBytes / 1024;

        if (requestedBufferKb != effectiveBufferKb)
        {
            parts.Add(
                $"{SocketBufferVariable}={requestedBufferKb}->{effectiveBufferKb}");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    public static int ClampWindowBlocks(int requested) =>
        Math.Clamp(requested, 1, FileTransferProtocol.MaxWindowSize);

    public static int ClampAckBlocks(int requested, int windowBlocks) =>
        Math.Clamp(requested, 1, Math.Max(1, windowBlocks));

    public static int ClampSocketBufferBytes(long requested) =>
        (int)Math.Clamp(requested, 0L, MaxSocketBufferBytes);

    private static bool IsEnabled(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim() is "1" or "true" or "TRUE" or "True" or
            "yes" or "YES" or "on" or "ON";
    }

    private static int? ReadInteger(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value.Trim(), out var parsed) ? parsed : null;
    }
}

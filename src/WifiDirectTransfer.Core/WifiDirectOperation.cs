namespace WifiDirectTransfer.Core;

/// <summary>
/// 错误映射时用于判断超时类型和取消来源的操作类别。
/// </summary>
public enum WifiDirectOperation
{
    None = 0,
    Discovery = 1,
    Connection = 2,
    Transfer = 3
}

/// <summary>
/// 错误映射时使用的调用上下文。
/// </summary>
public enum WifiDirectErrorContext
{
    Unknown = 0,
    WinRt = 1,
    FileIo = 2
}

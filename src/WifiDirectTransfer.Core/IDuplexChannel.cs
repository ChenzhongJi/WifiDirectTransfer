namespace WifiDirectTransfer.Core;

/// <summary>
/// 传输层唯一依赖的双向字节流通道。释放时应先刷新输出，再关闭底层连接。
/// </summary>
public interface IDuplexChannel : IAsyncDisposable
{
    Stream Input { get; }

    Stream Output { get; }

    bool IsConnected { get; }
}

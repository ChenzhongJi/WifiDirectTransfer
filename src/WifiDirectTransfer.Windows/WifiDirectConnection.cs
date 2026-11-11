using System.IO;
using WifiDirectTransfer.Core;
using Windows.Networking.Sockets;

namespace WifiDirectTransfer.Windows;

/// <summary>
/// 对 PeerFinder.ConnectAsync 返回的 StreamSocket 的 IDuplexChannel 包装。
/// </summary>
internal sealed class WifiDirectConnection : IDuplexChannel
{
    private readonly StreamSocket _socket;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Action<WifiDirectConnection>? _onDisposed;
    private int _disposed;

    public WifiDirectConnection(StreamSocket socket, Action<WifiDirectConnection>? onDisposed)
    {
        ArgumentNullException.ThrowIfNull(socket);

        _socket = socket;
        _onDisposed = onDisposed;

        try
        {
            // .NET 8 的投影提供 AsStreamForRead，而不是 IInputStream.AsStream(InputStreamOptions)。
            // 使用 256 KiB 内部缓冲，避免每个帧头和 payload 都触发独立的 WinRT 读取。
            _input = socket.InputStream.AsStreamForRead(
                bufferSize: 256 * 1024);
            _output = socket.OutputStream.AsStreamForWrite();
            EndpointInfo = ReadEndpointInfo(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public Stream Input => _input;

    public Stream Output => _output;

    public bool IsConnected => Volatile.Read(ref _disposed) == 0;

    internal WifiDirectEndpointInfo EndpointInfo { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await Output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 连接已断开时 Flush 可能失败，不应阻止关闭 Socket。
        }
        finally
        {
            try
            {
                _socket.Dispose();
            }
            finally
            {
                _onDisposed?.Invoke(this);
            }
        }
    }

    private static WifiDirectEndpointInfo ReadEndpointInfo(StreamSocket socket)
    {
        var information = socket.Information;

        // StreamSocketInformation 没有 LocalHostName，LocalAddress 是最接近的本地端点。
        return new WifiDirectEndpointInfo(
            information.LocalAddress?.ToString() ?? string.Empty,
            information.LocalPort ?? string.Empty,
            information.RemoteHostName?.ToString() ?? string.Empty,
            information.RemotePort ?? string.Empty);
    }
}

using System.Buffers.Binary;

namespace WifiDirectTransfer.Core;

public delegate Task AsyncMessageHandler(
    MessageDispatcher sender,
    MessageReceivedEventArgs args,
    CancellationToken cancellationToken);

/// <summary>
/// M1 帧复用器：所有消息共用同一对输入/输出流。
/// 帧格式：[MessageType:1][PayloadLength:4 big-endian][Payload]。
/// </summary>
public sealed class MessageDispatcher
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly int _maxPayloadLength;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private int _receiveStarted;
    private int _disconnected;
    private int _sendFaulted;

    public MessageDispatcher(
        Stream input,
        Stream output,
        int maxPayloadLength = MessageProtocol.DefaultMaxPayloadLength)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        if (!input.CanRead)
        {
            throw new ArgumentException("输入流必须可读。", nameof(input));
        }

        if (!output.CanWrite)
        {
            throw new ArgumentException("输出流必须可写。", nameof(output));
        }

        if (maxPayloadLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPayloadLength),
                "最大帧 payload 必须大于零。");
        }

        _input = input;
        _output = output;
        _maxPayloadLength = maxPayloadLength;
    }

    public int MaxPayloadLength => _maxPayloadLength;

    /// <summary>
    /// 处理器按订阅顺序逐个 await。Payload 仅在处理器执行期间有效。
    /// </summary>
    public event AsyncMessageHandler? MessageReceived;

    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    public Task SendAsync(
        MessageType type,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return SendAsync(type, payload.AsMemory(), cancellationToken);
    }

    public async Task SendAsync(
        MessageType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (!MessageProtocol.IsDefined(type))
        {
            throw new MessageProtocolException(
                $"未知的 MessageType：0x{(byte)type:X2}。");
        }

        if (payload.Length > _maxPayloadLength)
        {
            throw new MessageProtocolException(
                $"payload 长度 {payload.Length} 超过上限 {_maxPayloadLength}。");
        }

        if (Volatile.Read(ref _disconnected) != 0)
        {
            throw new InvalidOperationException("消息连接已断开。");
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Volatile.Read(ref _sendFaulted) != 0)
            {
                throw new InvalidOperationException("消息连接写入已损坏，必须重建连接。");
            }

            if (Volatile.Read(ref _disconnected) != 0)
            {
                throw new InvalidOperationException("消息连接已断开。");
            }

            var header = new byte[MessageProtocol.FrameHeaderLength];
            header[0] = (byte)type;
            BinaryPrimitives.WriteUInt32BigEndian(
                header.AsSpan(1, 4),
                (uint)payload.Length);

            try
            {
                await _output
                    .WriteAsync(header.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                await _output
                    .WriteAsync(payload, cancellationToken)
                    .ConfigureAwait(false);
                await _output
                    .FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // 帧可能只写出一部分，后续不能再复用这条流。
                Interlocked.Exchange(ref _sendFaulted, 1);
                throw;
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task StartReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _receiveStarted, 1) != 0)
        {
            throw new InvalidOperationException("接收循环已经启动。");
        }

        var typeBuffer = new byte[1];
        var lengthBuffer = new byte[4];

        try
        {
            while (true)
            {
                var firstByteRead = await _input
                    .ReadAsync(typeBuffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);

                if (firstByteRead == 0)
                {
                    RaiseDisconnected(error: null, remoteClosed: true);
                    return;
                }

                await _input
                    .ReadExactlyAsync(lengthBuffer, cancellationToken)
                    .ConfigureAwait(false);

                var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBuffer);

                if (payloadLength > (uint)_maxPayloadLength)
                {
                    throw new MessageProtocolException(
                        $"帧 payload 长度 {payloadLength} 超过上限 {_maxPayloadLength}。");
                }

                var type = (MessageType)typeBuffer[0];

                if (!MessageProtocol.IsDefined(type))
                {
                    throw new MessageProtocolException(
                        $"未知的 MessageType：0x{typeBuffer[0]:X2}。");
                }

                var payload = new byte[(int)payloadLength];

                await _input
                    .ReadExactlyAsync(payload, cancellationToken)
                    .ConfigureAwait(false);

                var args = new MessageReceivedEventArgs(type, payload);

                await InvokeMessageHandlersAsync(args, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 本地取消：不产生 Disconnected。
        }
        catch (Exception exception)
        {
            RaiseDisconnected(exception, remoteClosed: false);
        }
    }

    private async Task InvokeMessageHandlersAsync(
        MessageReceivedEventArgs args,
        CancellationToken cancellationToken)
    {
        var handlers = MessageReceived;

        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            await ((AsyncMessageHandler)handler)(this, args, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void RaiseDisconnected(Exception? error, bool remoteClosed)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0)
        {
            return;
        }

        var handlers = Disconnected;

        if (handlers is null)
        {
            return;
        }

        var args = new DisconnectedEventArgs(error, remoteClosed);

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<DisconnectedEventArgs>)handler)(this, args);
            }
            catch (Exception)
            {
                // 连接已经结束，订阅方异常不应影响其他订阅方。
            }
        }
    }
}

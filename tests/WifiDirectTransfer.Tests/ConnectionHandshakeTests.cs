using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class ConnectionHandshakeTests
{
    [Fact]
    public async Task Exchange_TwoPeers_ReturnsPeerBuildAndLimits()
    {
        await using var transport = new TestDuplexTransport();
        var left = new MessageDispatcher(
            transport.SenderInput,
            transport.SenderOutput,
            2_000_000);
        var right = new MessageDispatcher(
            transport.ReceiverInput,
            transport.ReceiverOutput,
            2_000_000);

        using var leftHandshake =
            new ConnectionHandshake(left, "build-left", 2_000_000);
        using var rightHandshake =
            new ConnectionHandshake(right, "build-right", 2_000_000);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var leftLoop = left.StartReceiveLoopAsync(cts.Token);
        var rightLoop = right.StartReceiveLoopAsync(cts.Token);

        try
        {
            var leftTask = leftHandshake.ExchangeAsync(cts.Token);
            var rightTask = rightHandshake.ExchangeAsync(cts.Token);

            var leftPeer = await leftTask;
            var rightPeer = await rightTask;

            Assert.Equal(MessageProtocol.ProtocolVersion, leftPeer.ProtocolVersion);
            Assert.Equal(MessageProtocol.ProtocolVersion, rightPeer.ProtocolVersion);
            Assert.Equal("build-right", leftPeer.Build);
            Assert.Equal("build-left", rightPeer.Build);
            Assert.Equal(2_000_000, leftPeer.MaxPayloadLength);
            Assert.Equal(2_000_000, rightPeer.MaxPayloadLength);
        }
        finally
        {
            await StopAsync(cts, transport, leftLoop, rightLoop);
        }
    }

    [Fact]
    public async Task Exchange_VersionMismatch_Throws()
    {
        await using var transport = new TestDuplexTransport();
        var left = new MessageDispatcher(
            transport.SenderInput,
            transport.SenderOutput,
            2_000_000);
        var right = new MessageDispatcher(
            transport.ReceiverInput,
            transport.ReceiverOutput,
            2_000_000);

        right.MessageReceived += async (sender, args, cancellationToken) =>
        {
            if (args.Type != MessageType.ControlMessage)
            {
                return;
            }

            var message = ControlMessageCodec.Deserialize(args.Payload.Span);

            if (message.Subtype == ControlMessageSubtype.Hello)
            {
                await sender.SendAsync(
                    MessageType.ControlMessage,
                    ControlMessageCodec.SerializeHello(
                        999,
                        2_000_000,
                        "old-build"),
                    cancellationToken);
            }
        };

        using var leftHandshake =
            new ConnectionHandshake(
                left,
                "build-left",
                2_000_000,
                TimeSpan.FromSeconds(2));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var leftLoop = left.StartReceiveLoopAsync(cts.Token);
        var rightLoop = right.StartReceiveLoopAsync(cts.Token);

        try
        {
            var exception = await Assert.ThrowsAsync<
                ProtocolVersionMismatchException>(
                () => leftHandshake.ExchangeAsync(cts.Token));

            Assert.Equal(MessageProtocol.ProtocolVersion, exception.LocalVersion);
            Assert.Equal(999, exception.PeerVersion);
        }
        finally
        {
            await StopAsync(cts, transport, leftLoop, rightLoop);
        }
    }

    [Fact]
    public async Task Exchange_NoPeerResponse_TimesOut()
    {
        await using var transport = new TestDuplexTransport();
        var left = new MessageDispatcher(
            transport.SenderInput,
            transport.SenderOutput,
            2_000_000);

        using var handshake =
            new ConnectionHandshake(
                left,
                "build-left",
                2_000_000,
                TimeSpan.FromMilliseconds(200));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var leftLoop = left.StartReceiveLoopAsync(cts.Token);

        try
        {
            await Assert.ThrowsAsync<HandshakeTimeoutException>(
                () => handshake.ExchangeAsync(cts.Token));
        }
        finally
        {
            await StopAsync(cts, transport, leftLoop);
        }
    }

    private static async Task StopAsync(
        CancellationTokenSource cts,
        TestDuplexTransport transport,
        params Task[] loops)
    {
        cts.Cancel();
        transport.CompleteBoth();

        foreach (var loop in loops)
        {
            try
            {
                await loop;
            }
            catch (Exception)
            {
                // 测试清理忽略循环退出异常。
            }
        }
    }
}

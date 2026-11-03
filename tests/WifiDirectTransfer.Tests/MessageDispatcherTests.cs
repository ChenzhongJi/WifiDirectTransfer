using System.Buffers.Binary;
using System.Text;
using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class MessageDispatcherTests
{
    [Fact]
    public async Task SendAsync_WritesFrameTypeAndBigEndianLength()
    {
        using var wire = new MemoryStream();
        var dispatcher = new MessageDispatcher(Stream.Null, wire);
        var payload = Enumerable.Range(0, 300).Select(value => (byte)(value % 256)).ToArray();

        await dispatcher.SendAsync(MessageType.TextMessage, payload, CancellationToken.None);

        var bytes = wire.ToArray();

        Assert.Equal(MessageProtocol.FrameHeaderLength + payload.Length, bytes.Length);
        Assert.Equal((byte)MessageType.TextMessage, bytes[0]);
        Assert.Equal(
            (uint)payload.Length,
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1, 4)));
        Assert.Equal(payload, bytes[5..]);
    }

    [Fact]
    public async Task ReceiveLoop_ReadsConsecutiveFramesWithoutBleedingPayload()
    {
        using var wire = new MemoryStream();
        var sender = new MessageDispatcher(Stream.Null, wire);

        var fakeFrameHeaderPayload = new byte[]
        {
            (byte)MessageType.FileEnd,
            0x00, 0x00, 0x00, 0x20,
            0xAA, 0xBB, 0xCC
        };

        await sender.SendAsync(
            MessageType.TextMessage,
            fakeFrameHeaderPayload,
            CancellationToken.None);
        await sender.SendAsync(
            MessageType.FileEnd,
            new byte[MessageProtocol.FileEndDigestLength],
            CancellationToken.None);
        await sender.SendAsync(
            MessageType.TextMessage,
            TextMessageCodec.Encode("tail"),
            CancellationToken.None);

        var messages = new List<(MessageType Type, byte[] Payload)>();
        DisconnectedEventArgs? disconnected = null;
        var receiver = CreateReceiver(wire, messages, args => disconnected = args);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await receiver.StartReceiveLoopAsync(cts.Token);

        Assert.Equal(3, messages.Count);
        Assert.Equal(MessageType.TextMessage, messages[0].Type);
        Assert.Equal(fakeFrameHeaderPayload, messages[0].Payload);
        Assert.Equal(MessageType.FileEnd, messages[1].Type);
        Assert.Equal(MessageProtocol.FileEndDigestLength, messages[1].Payload.Length);
        Assert.Equal(MessageType.TextMessage, messages[2].Type);
        Assert.Equal("tail", Encoding.UTF8.GetString(messages[2].Payload));
        Assert.NotNull(disconnected);
        Assert.True(disconnected!.RemoteClosed);
        Assert.Null(disconnected.Error);
    }

    [Fact]
    public async Task ReceiveLoop_ZeroLengthPayload_DoesNotConsumeNextFrame()
    {
        using var wire = new MemoryStream();
        var sender = new MessageDispatcher(Stream.Null, wire);

        await sender.SendAsync(MessageType.TextMessage, Array.Empty<byte>(), CancellationToken.None);
        await sender.SendAsync(
            MessageType.TextMessage,
            TextMessageCodec.Encode("next"),
            CancellationToken.None);

        var messages = new List<(MessageType Type, byte[] Payload)>();
        var receiver = CreateReceiver(wire, messages);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await receiver.StartReceiveLoopAsync(cts.Token);

        Assert.Equal(2, messages.Count);
        Assert.Empty(messages[0].Payload);
        Assert.Equal("next", Encoding.UTF8.GetString(messages[1].Payload));
    }

    [Fact]
    public async Task ReceiveLoop_TruncatedPayload_FiresDisconnected()
    {
        using var wire = new MemoryStream();
        wire.WriteByte((byte)MessageType.TextMessage);
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, 10);
        wire.Write(length);
        wire.Write(new byte[5]);
        wire.Position = 0;

        var messages = new List<(MessageType Type, byte[] Payload)>();
        DisconnectedEventArgs? disconnected = null;
        var receiver = new MessageDispatcher(wire, Stream.Null);
        receiver.MessageReceived += (_, args, _) =>
        {
            messages.Add((args.Type, args.Payload.ToArray()));
            return Task.CompletedTask;
        };
        receiver.Disconnected += (_, args) => disconnected = args;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await receiver.StartReceiveLoopAsync(cts.Token);

        Assert.Empty(messages);
        Assert.NotNull(disconnected);
        Assert.False(disconnected!.RemoteClosed);
        Assert.NotNull(disconnected.Error);
    }

    [Fact]
    public async Task ReceiveLoop_UnknownType_FiresDisconnected()
    {
        using var wire = new MemoryStream();
        wire.WriteByte(0x42);
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, 0);
        wire.Write(length);
        wire.Position = 0;

        DisconnectedEventArgs? disconnected = null;
        var receiver = new MessageDispatcher(wire, Stream.Null);
        receiver.Disconnected += (_, args) => disconnected = args;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await receiver.StartReceiveLoopAsync(cts.Token);

        Assert.NotNull(disconnected);
        Assert.NotNull(disconnected!.Error);
        Assert.IsType<MessageProtocolException>(disconnected.Error);
    }

    [Fact]
    public async Task ReceiveLoop_OversizedPayload_FiresDisconnected()
    {
        using var wire = new MemoryStream();
        wire.WriteByte((byte)MessageType.TextMessage);
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, 17);
        wire.Write(length);
        wire.Position = 0;

        DisconnectedEventArgs? disconnected = null;
        var receiver = new MessageDispatcher(wire, Stream.Null, maxPayloadLength: 16);
        receiver.Disconnected += (_, args) => disconnected = args;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await receiver.StartReceiveLoopAsync(cts.Token);

        Assert.NotNull(disconnected);
        Assert.IsType<MessageProtocolException>(disconnected!.Error);
    }

    [Fact]
    public async Task ReceiveLoop_HandlerException_FiresDisconnected()
    {
        using var wire = new MemoryStream();
        var sender = new MessageDispatcher(Stream.Null, wire);
        await sender.SendAsync(
            MessageType.TextMessage,
            TextMessageCodec.Encode("boom"),
            CancellationToken.None);

        wire.Position = 0;
        var receiver = new MessageDispatcher(wire, Stream.Null);
        DisconnectedEventArgs? disconnected = null;
        receiver.Disconnected += (_, args) => disconnected = args;
        receiver.MessageReceived += (_, _, _) =>
            throw new InvalidOperationException("handler failed");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await receiver.StartReceiveLoopAsync(cts.Token);

        Assert.NotNull(disconnected);
        Assert.IsType<InvalidOperationException>(disconnected!.Error);
    }

    [Fact]
    public async Task SendAsync_ConcurrentSends_DoNotInterleaveFrames()
    {
        using var wire = new MemoryStream();
        var dispatcher = new MessageDispatcher(Stream.Null, wire);
        var payloadA = Enumerable.Repeat((byte)0xAA, 300).ToArray();
        var payloadB = Enumerable.Repeat((byte)0xBB, 301).ToArray();

        await Task.WhenAll(
            dispatcher.SendAsync(MessageType.TextMessage, payloadA, CancellationToken.None),
            dispatcher.SendAsync(MessageType.TextMessage, payloadB, CancellationToken.None));

        var bytes = wire.ToArray();
        var frames = new List<byte[]>();
        var offset = 0;

        while (offset < bytes.Length)
        {
            var payloadLength = (int)BinaryPrimitives.ReadUInt32BigEndian(
                bytes.AsSpan(offset + 1, 4));
            offset += MessageProtocol.FrameHeaderLength;

            frames.Add(bytes.AsSpan(offset, payloadLength).ToArray());
            offset += payloadLength;
        }

        Assert.Equal(2, frames.Count);
        Assert.Contains(frames, frame => frame.SequenceEqual(payloadA));
        Assert.Contains(frames, frame => frame.SequenceEqual(payloadB));
    }

    [Fact]
    public async Task SendAsync_RejectsPayloadAboveConfiguredMaximum()
    {
        using var wire = new MemoryStream();
        var dispatcher = new MessageDispatcher(
            Stream.Null,
            wire,
            maxPayloadLength: 4);

        await Assert.ThrowsAsync<MessageProtocolException>(
            () => dispatcher.SendAsync(
                MessageType.TextMessage,
                new byte[5],
                CancellationToken.None));
    }

    private static MessageDispatcher CreateReceiver(
        MemoryStream wire,
        List<(MessageType Type, byte[] Payload)> messages,
        Action<DisconnectedEventArgs>? onDisconnected = null)
    {
        wire.Position = 0;

        var receiver = new MessageDispatcher(wire, Stream.Null);

        receiver.MessageReceived += (_, args, _) =>
        {
            messages.Add((args.Type, args.Payload.ToArray()));
            return Task.CompletedTask;
        };

        if (onDisconnected is not null)
        {
            receiver.Disconnected += (_, args) => onDisconnected(args);
        }

        return receiver;
    }
}

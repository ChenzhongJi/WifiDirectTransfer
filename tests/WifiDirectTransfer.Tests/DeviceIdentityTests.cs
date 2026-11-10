using System.Security.Cryptography;
using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class DeviceIdentityTests
{
    [Fact]
    public async Task Exchange_TwoPeers_ProducesMatchingCodeAndFingerprints()
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
        using var leftIdentity = new TestIdentity("左端");
        using var rightIdentity = new TestIdentity("右端");

        using var leftExchange = new DeviceIdentityExchange(
            left,
            leftIdentity,
            isInitiator: true);
        using var rightExchange = new DeviceIdentityExchange(
            right,
            rightIdentity,
            isInitiator: false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var leftLoop = left.StartReceiveLoopAsync(cts.Token);
        var rightLoop = right.StartReceiveLoopAsync(cts.Token);

        try
        {
            var leftTask = leftExchange.ExchangeAsync(cts.Token);
            var rightTask = rightExchange.ExchangeAsync(cts.Token);

            var leftPeer = await leftTask;
            var rightPeer = await rightTask;

            Assert.Equal("右端", leftPeer.DisplayName);
            Assert.Equal("左端", rightPeer.DisplayName);
            Assert.Equal(leftPeer.PairingCode, rightPeer.PairingCode);
            Assert.Matches(@"^\d{3} \d{3}$", leftPeer.PairingCode);
            Assert.Equal(
                DeviceIdentityCrypto.ComputeFingerprint(
                    rightIdentity.PublicKey),
                leftPeer.Fingerprint);
            Assert.Equal(
                DeviceIdentityCrypto.ComputeFingerprint(
                    leftIdentity.PublicKey),
                rightPeer.Fingerprint);
            Assert.Equal(
                rightIdentity.PublicKey,
                leftPeer.PublicKey);
            Assert.Equal(
                leftIdentity.PublicKey,
                rightPeer.PublicKey);

            var leftSeesRightTrusted = leftExchange.ExchangeTrustStateAsync(
                localTrusted: true,
                cts.Token);
            var rightSeesLeftTrusted = rightExchange.ExchangeTrustStateAsync(
                localTrusted: false,
                cts.Token);

            Assert.False(await leftSeesRightTrusted);
            Assert.True(await rightSeesLeftTrusted);
        }
        finally
        {
            await StopAsync(cts, transport, leftLoop, rightLoop);
        }
    }

    [Fact]
    public async Task Exchange_InvalidProof_ThrowsVerificationException()
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
        using var leftIdentity = new TestIdentity("左端");
        using var rightIdentity = new TestIdentity("恶意端");

        right.MessageReceived += async (sender, args, cancellationToken) =>
        {
            if (args.Type != MessageType.ControlMessage ||
                args.Payload.Length < 1)
            {
                return;
            }

            var subtype = (ControlMessageSubtype)args.Payload.Span[0];

            if (subtype == ControlMessageSubtype.IdentityHello)
            {
                var hello = DeviceIdentityCodec.DeserializeHello(
                    args.Payload.Span);
                var response = DeviceIdentityCodec.SerializeHello(
                    RandomNumberGenerator.GetBytes(
                        DeviceIdentityCodec.NonceLength),
                    rightIdentity.PublicKey,
                    rightIdentity.DisplayName);

                await sender.SendAsync(
                    MessageType.ControlMessage,
                    response,
                    cancellationToken);

                // 特意忽略对方 nonce，使用无关数据生成无效签名。
                _ = hello;
            }
            else if (subtype == ControlMessageSubtype.IdentityProof)
            {
                var invalidProof = DeviceIdentityCodec.SerializeProof(
                    new byte[64]);

                await sender.SendAsync(
                    MessageType.ControlMessage,
                    invalidProof,
                    cancellationToken);
            }
        };

        using var exchange = new DeviceIdentityExchange(
            left,
            leftIdentity,
            isInitiator: true,
            TimeSpan.FromSeconds(2));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var leftLoop = left.StartReceiveLoopAsync(cts.Token);
        var rightLoop = right.StartReceiveLoopAsync(cts.Token);

        try
        {
            await Assert.ThrowsAsync<DeviceIdentityVerificationException>(
                () => exchange.ExchangeAsync(cts.Token));
        }
        finally
        {
            await StopAsync(cts, transport, leftLoop, rightLoop);
        }
    }

    [Fact]
    public async Task Exchange_NoResponse_TimesOut()
    {
        await using var transport = new TestDuplexTransport();
        var left = new MessageDispatcher(
            transport.SenderInput,
            transport.SenderOutput,
            2_000_000);
        using var identity = new TestIdentity("左端");
        using var exchange = new DeviceIdentityExchange(
            left,
            identity,
            isInitiator: true,
            TimeSpan.FromMilliseconds(200));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var leftLoop = left.StartReceiveLoopAsync(cts.Token);

        try
        {
            await Assert.ThrowsAsync<DeviceIdentityTimeoutException>(
                () => exchange.ExchangeAsync(cts.Token));
        }
        finally
        {
            await StopAsync(cts, transport, leftLoop);
        }
    }

    [Fact]
    public async Task ExchangeTrustState_NoPeerResponse_TimesOut()
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
        using var leftIdentity = new TestIdentity("左端");
        using var rightIdentity = new TestIdentity("右端");
        using var leftExchange = new DeviceIdentityExchange(
            left,
            leftIdentity,
            isInitiator: true,
            TimeSpan.FromMilliseconds(300));
        using var rightExchange = new DeviceIdentityExchange(
            right,
            rightIdentity,
            isInitiator: false,
            TimeSpan.FromMilliseconds(300));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var leftLoop = left.StartReceiveLoopAsync(cts.Token);
        var rightLoop = right.StartReceiveLoopAsync(cts.Token);

        try
        {
            await Task.WhenAll(
                leftExchange.ExchangeAsync(cts.Token),
                rightExchange.ExchangeAsync(cts.Token));

            await Assert.ThrowsAsync<DeviceIdentityTimeoutException>(
                () => leftExchange.ExchangeTrustStateAsync(
                    localTrusted: true,
                    cts.Token));
        }
        finally
        {
            await StopAsync(cts, transport, leftLoop, rightLoop);
        }
    }

    [Fact]
    public void Hello_RoundTrips()
    {
        var nonce = RandomNumberGenerator.GetBytes(
            DeviceIdentityCodec.NonceLength);
        var publicKey = RandomNumberGenerator.GetBytes(91);
        const string displayName = "测试电脑";

        var payload = DeviceIdentityCodec.SerializeHello(
            nonce,
            publicKey,
            displayName);
        var hello = DeviceIdentityCodec.DeserializeHello(payload);

        Assert.Equal(nonce, hello.Nonce);
        Assert.Equal(publicKey, hello.PublicKey);
        Assert.Equal(displayName, hello.DisplayName);
        Assert.Equal(
            (byte)ControlMessageSubtype.IdentityHello,
            payload[0]);
    }

    [Fact]
    public void Hello_InvalidNonceLength_Throws()
    {
        Assert.Throws<MessageProtocolException>(
            () => DeviceIdentityCodec.SerializeHello(
                new byte[1],
                RandomNumberGenerator.GetBytes(91),
                "测试电脑"));
    }

    [Fact]
    public void Codec_RejectsWrongSubtypeAndTruncatedProof()
    {
        var hello = DeviceIdentityCodec.SerializeHello(
            RandomNumberGenerator.GetBytes(DeviceIdentityCodec.NonceLength),
            RandomNumberGenerator.GetBytes(91),
            "测试电脑");

        hello[0] = (byte)ControlMessageSubtype.IdentityProof;

        Assert.Throws<MessageProtocolException>(
            () => DeviceIdentityCodec.DeserializeHello(hello));
        Assert.Throws<MessageProtocolException>(
            () => DeviceIdentityCodec.DeserializeProof(new byte[2]));
    }

    [Fact]
    public void TrustState_RoundTripsAndRejectsInvalidPayload()
    {
        Assert.True(DeviceIdentityCodec.DeserializeTrustState(
            DeviceIdentityCodec.SerializeTrustState(true)));
        Assert.False(DeviceIdentityCodec.DeserializeTrustState(
            DeviceIdentityCodec.SerializeTrustState(false)));

        Assert.Throws<MessageProtocolException>(
            () => DeviceIdentityCodec.DeserializeTrustState(new byte[1]));

        Assert.Throws<MessageProtocolException>(
            () => DeviceIdentityCodec.DeserializeTrustState(new byte[]
            {
                (byte)ControlMessageSubtype.IdentityTrust,
                2
            }));
    }

    [Fact]
    public void PairingCode_DependsOnBothKeysAndNonces()
    {
        var leftNonce = RandomNumberGenerator.GetBytes(32);
        var rightNonce = RandomNumberGenerator.GetBytes(32);
        var leftKey = RandomNumberGenerator.GetBytes(91);
        var rightKey = RandomNumberGenerator.GetBytes(91);

        var code = DeviceIdentityCrypto.ComputePairingCode(
            leftNonce,
            rightNonce,
            leftKey,
            rightKey);

        Assert.Equal(
            code,
            DeviceIdentityCrypto.ComputePairingCode(
                leftNonce,
                rightNonce,
                leftKey,
                rightKey));
        Assert.NotEqual(
            code,
            DeviceIdentityCrypto.ComputePairingCode(
                leftNonce,
                rightNonce,
                leftKey,
                RandomNumberGenerator.GetBytes(91)));

        var initiatorNonce = leftNonce;
        var responderNonce = rightNonce;
        var initiatorKey = leftKey;
        var responderKey = rightKey;

        Assert.Equal(
            code,
            DeviceIdentityCrypto.ComputePairingCode(
                initiatorNonce,
                responderNonce,
                initiatorKey,
                responderKey));
    }

    [Fact]
    public void Fingerprint_IsStableAndFormattedInGroups()
    {
        var publicKey = RandomNumberGenerator.GetBytes(91);
        var fingerprint = DeviceIdentityCrypto.ComputeFingerprint(publicKey);

        Assert.Equal(64, fingerprint.Length);
        Assert.Equal(
            fingerprint,
            DeviceIdentityCrypto.ComputeFingerprint(publicKey));

        var formatted = DeviceIdentityCrypto.FormatFingerprint(fingerprint);
        Assert.Equal(fingerprint, formatted.Replace(" ", string.Empty));
        Assert.Equal(16, formatted.Split(' ').Length);
    }

    private sealed class TestIdentity : IDeviceIdentity, IDisposable
    {
        private readonly ECDsa _ecdsa = ECDsa.Create(
            ECCurve.NamedCurves.nistP256);

        public TestIdentity(string displayName)
        {
            DisplayName = displayName;
        }

        public string DisplayName { get; }

        public byte[] PublicKey => _ecdsa.ExportSubjectPublicKeyInfo();

        public byte[] SignData(ReadOnlySpan<byte> data) =>
            _ecdsa.SignData(
                data,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);

        public void Dispose() => _ecdsa.Dispose();
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

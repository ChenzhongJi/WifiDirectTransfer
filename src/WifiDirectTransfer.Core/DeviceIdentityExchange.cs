using System.Security.Cryptography;

namespace WifiDirectTransfer.Core;

/// <summary>
/// 连接后交换 ECDSA 公钥和随机 nonce，双方互相签名对方 nonce，
/// 验证通过后生成相同的 6 位配对码。
/// </summary>
public sealed class DeviceIdentityExchange : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly MessageDispatcher _dispatcher;
    private readonly IDeviceIdentity _identity;
    private readonly bool _isInitiator;
    private readonly TimeSpan _timeout;
    private readonly TaskCompletionSource<DeviceIdentityHello> _peerHello =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<byte[]> _peerProof =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _peerTrustState =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _disposed;

    public DeviceIdentityExchange(
        MessageDispatcher dispatcher,
        IDeviceIdentity identity,
        bool isInitiator,
        TimeSpan? timeout = null)
    {
        _dispatcher = dispatcher ??
                      throw new ArgumentNullException(nameof(dispatcher));
        _identity = identity ??
                    throw new ArgumentNullException(nameof(identity));
        _isInitiator = isInitiator;
        _timeout = timeout ?? DefaultTimeout;

        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "身份交换超时必须大于零。");
        }

        _dispatcher.MessageReceived += OnMessageReceivedAsync;
    }

    public async Task<PeerDeviceIdentity> ExchangeAsync(
        CancellationToken cancellationToken = default)
    {
        var localNonce = RandomNumberGenerator.GetBytes(
            DeviceIdentityCodec.NonceLength);
        var hello = DeviceIdentityCodec.SerializeHello(
            localNonce,
            _identity.PublicKey,
            _identity.DisplayName);

        await _dispatcher
            .SendAsync(
                MessageType.ControlMessage,
                hello,
                cancellationToken)
            .ConfigureAwait(false);

        DeviceIdentityHello peerHello;

        try
        {
            peerHello = await _peerHello.Task
                .WaitAsync(_timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new DeviceIdentityTimeoutException();
        }

        var proofData = DeviceIdentityCrypto.CreateProofData(peerHello.Nonce);
        var proof = DeviceIdentityCrypto.Sign(_identity, proofData);

        await _dispatcher
            .SendAsync(
                MessageType.ControlMessage,
                DeviceIdentityCodec.SerializeProof(proof),
                cancellationToken)
            .ConfigureAwait(false);

        byte[] peerProof;

        try
        {
            peerProof = await _peerProof.Task
                .WaitAsync(_timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new DeviceIdentityTimeoutException();
        }

        var localProofData = DeviceIdentityCrypto.CreateProofData(localNonce);

        if (!DeviceIdentityCrypto.Verify(
                peerHello.PublicKey,
                localProofData,
                peerProof))
        {
            throw new DeviceIdentityVerificationException(
                "对端无法证明持有声明的设备私钥。");
        }

        var initiatorNonce = _isInitiator ? localNonce : peerHello.Nonce;
        var responderNonce = _isInitiator ? peerHello.Nonce : localNonce;
        var initiatorPublicKey = _isInitiator
            ? _identity.PublicKey
            : peerHello.PublicKey;
        var responderPublicKey = _isInitiator
            ? peerHello.PublicKey
            : _identity.PublicKey;

        return new PeerDeviceIdentity(
            peerHello.DisplayName,
            peerHello.PublicKey,
            DeviceIdentityCrypto.ComputeFingerprint(peerHello.PublicKey),
            DeviceIdentityCrypto.ComputePairingCode(
                initiatorNonce,
                responderNonce,
                initiatorPublicKey,
                responderPublicKey));
    }

    /// <summary>
    /// 双方交换本地是否已信任对方。任一端未信任时，两端都显示配对码，
    /// 避免只有一端需要配对而无法比对。
    /// </summary>
    public async Task<bool> ExchangeTrustStateAsync(
        bool localTrusted,
        CancellationToken cancellationToken = default)
    {
        await _dispatcher
            .SendAsync(
                MessageType.ControlMessage,
                DeviceIdentityCodec.SerializeTrustState(localTrusted),
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            return await _peerTrustState.Task
                .WaitAsync(_timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new DeviceIdentityTimeoutException(
                "设备信任状态交换超时，连接已断开。");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _dispatcher.MessageReceived -= OnMessageReceivedAsync;
        _peerHello.TrySetCanceled();
        _peerProof.TrySetCanceled();
        _peerTrustState.TrySetCanceled();
    }

    private Task OnMessageReceivedAsync(
        MessageDispatcher sender,
        MessageReceivedEventArgs args,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            args.Type != MessageType.ControlMessage ||
            args.Payload.Length < 1)
        {
            return Task.CompletedTask;
        }

        try
        {
            var subtype = (ControlMessageSubtype)args.Payload.Span[0];

            if (subtype == ControlMessageSubtype.IdentityHello)
            {
                _peerHello.TrySetResult(
                    DeviceIdentityCodec.DeserializeHello(args.Payload.Span));
            }
            else if (subtype == ControlMessageSubtype.IdentityProof)
            {
                _peerProof.TrySetResult(
                    DeviceIdentityCodec.DeserializeProof(args.Payload.Span));
            }
            else if (subtype == ControlMessageSubtype.IdentityTrust)
            {
                _peerTrustState.TrySetResult(
                    DeviceIdentityCodec.DeserializeTrustState(
                        args.Payload.Span));
            }
        }
        catch (Exception)
        {
            // 身份交换阶段的其他消息或坏帧直接忽略。
        }

        return Task.CompletedTask;
    }
}

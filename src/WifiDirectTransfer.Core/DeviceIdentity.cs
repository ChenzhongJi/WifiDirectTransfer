using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WifiDirectTransfer.Core;

/// <summary>本机设备身份：稳定的 ECDSA 公钥和签名能力。</summary>
public interface IDeviceIdentity
{
    string DisplayName { get; }

    byte[] PublicKey { get; }

    byte[] SignData(ReadOnlySpan<byte> data);
}

/// <summary>通过身份交换验证后的对端信息。</summary>
public sealed class PeerDeviceIdentity
{
    public PeerDeviceIdentity(
        string displayName,
        byte[] publicKey,
        string fingerprint,
        string pairingCode)
    {
        DisplayName = displayName;
        PublicKey = publicKey;
        Fingerprint = fingerprint;
        PairingCode = pairingCode;
    }

    public string DisplayName { get; }

    public byte[] PublicKey { get; }

    public string Fingerprint { get; }

    public string PairingCode { get; }
}

public static class DeviceIdentityCrypto
{
    private static readonly byte[] ProofPrefix =
        Encoding.UTF8.GetBytes("WDT-IDENTITY-PROOF-V1");

    private static readonly byte[] PairingPrefix =
        Encoding.UTF8.GetBytes("WDT-PAIR-V1");

    public static byte[] CreateProofData(ReadOnlySpan<byte> peerNonce)
    {
        var data = new byte[ProofPrefix.Length + peerNonce.Length];
        ProofPrefix.CopyTo(data, 0);
        peerNonce.CopyTo(data.AsSpan(ProofPrefix.Length));
        return data;
    }

    public static byte[] Sign(
        IDeviceIdentity identity,
        ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return identity.SignData(data);
    }

    public static bool Verify(
        byte[] publicKey,
        ReadOnlySpan<byte> data,
        byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(signature);

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            return ecdsa.VerifyData(
                data,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string ComputeFingerprint(byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        return Convert.ToHexString(SHA256.HashData(publicKey));
    }

    /// <summary>
    /// 把连续指纹格式化为 4 字符一组，便于用户逐段比对。
    /// </summary>
    public static string FormatFingerprint(string? fingerprint)
    {
        var normalized = TrustedDeviceStore.NormalizeFingerprint(fingerprint);

        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var groups = new List<string>((normalized.Length + 3) / 4);

        for (var offset = 0; offset < normalized.Length; offset += 4)
        {
            var length = Math.Min(4, normalized.Length - offset);
            groups.Add(normalized.Substring(offset, length));
        }

        return string.Join(' ', groups);
    }

    public static string ComputePairingCode(
        byte[] initiatorNonce,
        byte[] responderNonce,
        byte[] initiatorPublicKey,
        byte[] responderPublicKey)
    {
        ArgumentNullException.ThrowIfNull(initiatorNonce);
        ArgumentNullException.ThrowIfNull(responderNonce);
        ArgumentNullException.ThrowIfNull(initiatorPublicKey);
        ArgumentNullException.ThrowIfNull(responderPublicKey);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(PairingPrefix);
        hash.AppendData(initiatorNonce);
        hash.AppendData(responderNonce);
        hash.AppendData(initiatorPublicKey);
        hash.AppendData(responderPublicKey);
        var digest = hash.GetHashAndReset();
        var value = BinaryPrimitives.ReadUInt32BigEndian(digest) % 1_000_000;
        return $"{value / 1000:000} {value % 1000:000}";
    }
}

public sealed record DeviceIdentityHello(
    byte Version,
    byte[] Nonce,
    byte[] PublicKey,
    string DisplayName);

public static class DeviceIdentityCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public const byte Version = 1;
    public const int NonceLength = 32;
    public const int MaxPublicKeyLength = 1024;
    public const int MaxDisplayNameLength = 255;

    public static byte[] SerializeHello(
        byte[] nonce,
        byte[] publicKey,
        string displayName)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(displayName);

        if (nonce.Length != NonceLength)
        {
            throw new MessageProtocolException("身份 nonce 长度无效。");
        }

        if (publicKey.Length is < 1 or > MaxPublicKeyLength)
        {
            throw new MessageProtocolException("身份公钥长度无效。");
        }

        byte[] nameBytes;

        try
        {
            nameBytes = StrictUtf8.GetBytes(displayName);
        }
        catch (EncoderFallbackException exception)
        {
            throw new MessageProtocolException(
                "设备名称不是有效的 Unicode。",
                exception);
        }

        if (nameBytes.Length is < 1 or > MaxDisplayNameLength)
        {
            throw new MessageProtocolException("设备名称长度无效。");
        }

        var payload = new byte[
            1 + 1 + NonceLength + 2 + publicKey.Length + 2 + nameBytes.Length];
        var offset = 0;
        payload[offset++] = (byte)ControlMessageSubtype.IdentityHello;
        payload[offset++] = Version;
        nonce.CopyTo(payload, offset);
        offset += NonceLength;
        BinaryPrimitives.WriteUInt16BigEndian(
            payload.AsSpan(offset, 2),
            (ushort)publicKey.Length);
        offset += 2;
        publicKey.CopyTo(payload, offset);
        offset += publicKey.Length;
        BinaryPrimitives.WriteUInt16BigEndian(
            payload.AsSpan(offset, 2),
            (ushort)nameBytes.Length);
        offset += 2;
        nameBytes.CopyTo(payload, offset);
        return payload;
    }

    public static DeviceIdentityHello DeserializeHello(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1 + 1 + NonceLength + 2 + 2 ||
            payload[0] != (byte)ControlMessageSubtype.IdentityHello)
        {
            throw new MessageProtocolException("IdentityHello payload 无效。");
        }

        if (payload[1] != Version)
        {
            throw new MessageProtocolException("不支持的 IdentityHello 版本。");
        }

        var offset = 2;
        var nonce = payload.Slice(offset, NonceLength).ToArray();
        offset += NonceLength;
        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(
            payload.Slice(offset, 2));
        offset += 2;

        if (keyLength is < 1 or > MaxPublicKeyLength ||
            payload.Length - offset < keyLength + 2)
        {
            throw new MessageProtocolException("IdentityHello 公钥长度无效。");
        }

        var publicKey = payload.Slice(offset, keyLength).ToArray();
        offset += keyLength;
        var nameLength = BinaryPrimitives.ReadUInt16BigEndian(
            payload.Slice(offset, 2));
        offset += 2;

        if (nameLength is < 1 or > MaxDisplayNameLength ||
            payload.Length - offset != nameLength)
        {
            throw new MessageProtocolException("IdentityHello 设备名称长度无效。");
        }

        string displayName;

        try
        {
            displayName = StrictUtf8.GetString(payload.Slice(offset, nameLength));
        }
        catch (DecoderFallbackException exception)
        {
            throw new MessageProtocolException(
                "IdentityHello 设备名称不是有效的 UTF-8。",
                exception);
        }

        return new DeviceIdentityHello(
            payload[1],
            nonce,
            publicKey,
            displayName);
    }

    public static byte[] SerializeProof(byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(signature);

        if (signature.Length is < 1 or > 1024)
        {
            throw new MessageProtocolException("身份签名长度无效。");
        }

        var payload = new byte[1 + 2 + signature.Length];
        payload[0] = (byte)ControlMessageSubtype.IdentityProof;
        BinaryPrimitives.WriteUInt16BigEndian(
            payload.AsSpan(1, 2),
            (ushort)signature.Length);
        signature.CopyTo(payload, 3);
        return payload;
    }

    public static byte[] DeserializeProof(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3 ||
            payload[0] != (byte)ControlMessageSubtype.IdentityProof)
        {
            throw new MessageProtocolException("IdentityProof payload 无效。");
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(
            payload.Slice(1, 2));

        if (length is < 1 or > 1024 || payload.Length != 3 + length)
        {
            throw new MessageProtocolException("IdentityProof 签名长度无效。");
        }

        return payload.Slice(3, length).ToArray();
    }

    public static byte[] SerializeTrustState(bool trusted)
    {
        return new[]
        {
            (byte)ControlMessageSubtype.IdentityTrust,
            trusted ? (byte)1 : (byte)0
        };
    }

    public static bool DeserializeTrustState(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 2 ||
            payload[0] != (byte)ControlMessageSubtype.IdentityTrust ||
            payload[1] > 1)
        {
            throw new MessageProtocolException(
                "IdentityTrust payload 无效。");
        }

        return payload[1] == 1;
    }
}

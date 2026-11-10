using System.Security.Cryptography;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.Windows;

/// <summary>
/// 使用 Windows 用户密钥存储（CNG）保存 ECDSA P-256 设备私钥。
/// 私钥标记为不可导出，只暴露公钥和签名能力。
/// </summary>
public sealed class WindowsDeviceIdentity : IDeviceIdentity, IDisposable
{
    public const string DefaultKeyName = "WifiDirectTransfer.DeviceIdentity.v1";

    private readonly object _signGate = new();
    private readonly CngKey _key;
    private readonly ECDsaCng _ecdsa;
    private int _disposed;

    public WindowsDeviceIdentity(
        string displayName,
        string keyName = DefaultKeyName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException(
                "设备名称不能为空。",
                nameof(displayName));
        }

        if (string.IsNullOrWhiteSpace(keyName))
        {
            throw new ArgumentException(
                "密钥名称不能为空。",
                nameof(keyName));
        }

        DisplayName = displayName.Trim();
        _key = OpenOrCreateKey(keyName);
        _ecdsa = new ECDsaCng(_key);
        PublicKey = _ecdsa.ExportSubjectPublicKeyInfo();
    }

    public string DisplayName { get; }

    public byte[] PublicKey { get; }

    public string Fingerprint =>
        DeviceIdentityCrypto.ComputeFingerprint(PublicKey);

    public byte[] SignData(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        lock (_signGate)
        {
            return _ecdsa.SignData(
                data,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ecdsa.Dispose();
        _key.Dispose();
    }

    private static CngKey OpenOrCreateKey(string keyName)
    {
        try
        {
            return CngKey.Open(
                keyName,
                CngProvider.MicrosoftSoftwareKeyStorageProvider,
                CngKeyOpenOptions.UserKey);
        }
        catch (CryptographicException)
        {
            try
            {
                return CngKey.Create(
                    CngAlgorithm.ECDsaP256,
                    keyName,
                    new CngKeyCreationParameters
                    {
                        Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                        KeyUsage = CngKeyUsages.Signing,
                        ExportPolicy = CngExportPolicies.None,
                        KeyCreationOptions = CngKeyCreationOptions.None
                    });
            }
            catch (CryptographicException)
            {
                // 两个进程同时首次创建时可能竞争，再尝试打开一次。
                return CngKey.Open(
                    keyName,
                    CngProvider.MicrosoftSoftwareKeyStorageProvider,
                    CngKeyOpenOptions.UserKey);
            }
        }
    }
}

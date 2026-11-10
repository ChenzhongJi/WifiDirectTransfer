using System.Security.Cryptography;
using WifiDirectTransfer.Core;
using WifiDirectTransfer.Windows;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class WindowsDeviceIdentityTests
{
    [Fact]
    public void Constructor_CreatesPersistentNonExportableKey()
    {
        var keyName = "WifiDirectTransfer.Tests." + Guid.NewGuid().ToString("N");

        try
        {
            using var first = new WindowsDeviceIdentity("测试电脑", keyName);
            using var second = new WindowsDeviceIdentity("测试电脑", keyName);

            Assert.Equal(first.PublicKey, second.PublicKey);
            Assert.Equal(first.Fingerprint, second.Fingerprint);
            Assert.Equal(64, first.Fingerprint.Length);

            using var key = CngKey.Open(
                keyName,
                CngProvider.MicrosoftSoftwareKeyStorageProvider,
                CngKeyOpenOptions.UserKey);

            Assert.Equal(CngExportPolicies.None, key.ExportPolicy);
            Assert.ThrowsAny<CryptographicException>(
                () => key.Export(CngKeyBlobFormat.EccPrivateBlob));
        }
        finally
        {
            DeleteKey(keyName);
        }
    }

    [Fact]
    public void SignData_ProducesVerifiableSignature()
    {
        var keyName = "WifiDirectTransfer.Tests." + Guid.NewGuid().ToString("N");

        try
        {
            using var identity = new WindowsDeviceIdentity("测试电脑", keyName);
            var data = RandomNumberGenerator.GetBytes(64);
            var signature = identity.SignData(data);

            Assert.True(
                DeviceIdentityCrypto.Verify(
                    identity.PublicKey,
                    data,
                    signature));
        }
        finally
        {
            DeleteKey(keyName);
        }
    }

    private static void DeleteKey(string keyName)
    {
        try
        {
            using var key = CngKey.Open(
                keyName,
                CngProvider.MicrosoftSoftwareKeyStorageProvider,
                CngKeyOpenOptions.UserKey);
            key.Delete();
        }
        catch (CryptographicException)
        {
            // 创建失败时没有密钥需要清理。
        }
    }
}

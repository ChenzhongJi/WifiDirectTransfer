using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class TrustedDeviceStoreTests
{
    [Fact]
    public void DefaultPath_UsesLocalAppDataFile()
    {
        var store = new TrustedDeviceStore();

        Assert.Equal("trusted-devices.json", Path.GetFileName(store.FilePath));
        Assert.Equal(
            "WifiDirectTransfer",
            Path.GetFileName(Path.GetDirectoryName(store.FilePath)));
    }

    [Fact]
    public async Task AddOrUpdate_NormalizesFingerprintAndDeduplicates()
    {
        var directory = CreateTempDirectory();

        try
        {
            var path = Path.Combine(directory, "trusted-devices.json");
            var store = new TrustedDeviceStore(path);
            var trustedAt = DateTimeOffset.Now.AddDays(-1);

            Assert.True(store.AddOrUpdate(new TrustedDevice
            {
                Fingerprint = "aabb ccdd eeff 0011",
                PublicKey = "first-key",
                DisplayName = "电脑一",
                LastPeerId = "peer-1",
                LastPeerName = "电脑一",
                TrustedAt = trustedAt,
                LastConnectedAt = trustedAt
            }));
            Assert.True(store.AddOrUpdate(new TrustedDevice
            {
                Fingerprint = "AABBCCDDEEFF0011",
                PublicKey = "second-key",
                DisplayName = "电脑一（新）",
                LastPeerId = "peer-2",
                LastPeerName = "电脑一（新）",
                TrustedAt = DateTimeOffset.Now,
                LastConnectedAt = DateTimeOffset.Now
            }));

            var loaded = await Task.Run(store.Load);

            Assert.Single(loaded);
            Assert.Equal("AABBCCDDEEFF0011", loaded[0].Fingerprint);
            Assert.Equal("second-key", loaded[0].PublicKey);
            Assert.Equal("电脑一（新）", loaded[0].DisplayName);
            Assert.Equal("peer-2", loaded[0].LastPeerId);
            Assert.Equal(trustedAt, loaded[0].TrustedAt);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FindByPeerCandidate_PrefersExactId()
    {
        var directory = CreateTempDirectory();

        try
        {
            var store = new TrustedDeviceStore(
                Path.Combine(directory, "trusted-devices.json"));
            store.Save(new[]
            {
                CreateDevice("AAAAAAAAAAAAAAAA", "同名设备", "peer-1"),
                CreateDevice("BBBBBBBBBBBBBBBB", "同名设备", "peer-2")
            });

            var byId = store.FindByPeerCandidate("peer-2", "随便什么名字");

            Assert.NotNull(byId);
            Assert.Equal("BBBBBBBBBBBBBBBB", byId!.Fingerprint);

            // 显示名称有歧义时不预判可信设备。
            Assert.Null(store.FindByPeerCandidate(null, "同名设备"));
            Assert.Null(store.FindByPeerCandidate("unknown", "同名设备"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FindByPeerCandidate_UniqueNameFallback()
    {
        var directory = CreateTempDirectory();

        try
        {
            var store = new TrustedDeviceStore(
                Path.Combine(directory, "trusted-devices.json"));
            store.Save(new[]
            {
                CreateDevice("AAAAAAAAAAAAAAAA", "电脑一", "peer-1"),
                CreateDevice("BBBBBBBBBBBBBBBB", "电脑二", "peer-2")
            });

            var match = store.FindByPeerCandidate(
                "new-peer-id",
                "电脑二");

            Assert.NotNull(match);
            Assert.Equal("BBBBBBBBBBBBBBBB", match!.Fingerprint);
            Assert.Null(store.FindByPeerCandidate(
                "new-peer-id",
                PeerIdentityMatcher.UnnamedPeerDisplayName));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FindByFingerprint_AcceptsFormattedFingerprint()
    {
        var directory = CreateTempDirectory();

        try
        {
            var store = new TrustedDeviceStore(
                Path.Combine(directory, "trusted-devices.json"));
            store.Save(new[]
            {
                CreateDevice("AABBCCDDEEFF0011", "电脑一", "p1")
            });

            Assert.NotNull(store.FindByFingerprint(
                "AA BB CC DD EE FF 00 11"));
            Assert.Null(store.FindByFingerprint("not-a-fingerprint"));
            Assert.Null(store.FindByFingerprint(null));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Remove_DeletesOnlyMatchingFingerprint()
    {
        var directory = CreateTempDirectory();

        try
        {
            var store = new TrustedDeviceStore(
                Path.Combine(directory, "trusted-devices.json"));
            store.Save(new[]
            {
                CreateDevice("AAAAAAAAAAAAAAAA", "电脑一", "p1"),
                CreateDevice("BBBBBBBBBBBBBBBB", "电脑二", "p2")
            });

            Assert.True(store.Remove("AAAA AAAA AAAA AAAA"));
            var loaded = store.Load();

            Assert.Single(loaded);
            Assert.Equal("BBBBBBBBBBBBBBBB", loaded[0].Fingerprint);
            Assert.False(store.Remove("不存在"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_CorruptedFile_ReturnsEmpty()
    {
        var directory = CreateTempDirectory();

        try
        {
            var path = Path.Combine(directory, "trusted-devices.json");
            File.WriteAllText(path, "{ not json");
            var store = new TrustedDeviceStore(path);

            Assert.Empty(store.Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Save_IgnoresInvalidFingerprints()
    {
        var directory = CreateTempDirectory();

        try
        {
            var store = new TrustedDeviceStore(
                Path.Combine(directory, "trusted-devices.json"));

            Assert.True(store.Save(new[]
            {
                new TrustedDevice
                {
                    Fingerprint = "ABC",
                    DisplayName = "太短",
                    LastConnectedAt = DateTimeOffset.Now
                },
                CreateDevice("AABBCCDDEEFF0011", "有效", "p1")
            }));

            var loaded = store.Load();

            Assert.Single(loaded);
            Assert.Equal("有效", loaded[0].DisplayName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static TrustedDevice CreateDevice(
        string fingerprint,
        string displayName,
        string peerId) =>
        new()
        {
            Fingerprint = fingerprint,
            PublicKey = "key-" + peerId,
            DisplayName = displayName,
            LastPeerId = peerId,
            LastPeerName = displayName,
            TrustedAt = DateTimeOffset.Now.AddMinutes(-10),
            LastConnectedAt = DateTimeOffset.Now.AddMinutes(-5)
        };

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "wdt-trusted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

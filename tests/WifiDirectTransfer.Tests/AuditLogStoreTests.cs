using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class AuditLogStoreTests
{
    [Fact]
    public void DefaultPath_UsesLocalAppDataFile()
    {
        var store = new AuditLogStore();

        Assert.Equal("audit.jsonl", Path.GetFileName(store.FilePath));
        Assert.Equal(
            "WifiDirectTransfer",
            Path.GetFileName(Path.GetDirectoryName(store.FilePath)));
    }

    [Fact]
    public void AppendAndReadAll_RoundTripsEvents()
    {
        var directory = CreateTempDirectory();

        try
        {
            var path = Path.Combine(directory, "audit.jsonl");
            var store = new AuditLogStore(path);

            Assert.True(store.Append(
                AuditEventType.PairingAccepted,
                "电脑一",
                "aa bb cc dd",
                "用户确认配对码一致"));
            Assert.True(store.Append(
                AuditEventType.TrustAdded,
                "电脑一",
                "AABBCCDD",
                "配对时选择信任"));

            var entries = store.ReadAll();

            Assert.Equal(2, entries.Count);
            Assert.Equal(AuditEventType.PairingAccepted, entries[0].Event);
            Assert.Equal("电脑一", entries[0].PeerName);
            Assert.Equal("AABBCCDD", entries[0].Fingerprint);
            Assert.Equal("用户确认配对码一致", entries[0].Detail);
            Assert.Equal(AuditEventType.TrustAdded, entries[1].Event);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Append_RotatesWhenFileExceedsLimit()
    {
        var directory = CreateTempDirectory();

        try
        {
            var path = Path.Combine(directory, "audit.jsonl");
            var store = new AuditLogStore(
                path,
                maxFileBytes: 10,
                retainedFiles: 2);

            for (var index = 0; index < 5; index++)
            {
                Assert.True(store.Append(
                    AuditEventType.IdentityVerified,
                    "电脑" + index));
            }

            Assert.True(File.Exists(path));
            Assert.True(File.Exists(path + ".1"));
            Assert.False(File.Exists(path + ".2"));
            Assert.Equal(2, store.GetFilePaths().Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadAll_SkipsCorruptedLines()
    {
        var directory = CreateTempDirectory();

        try
        {
            var path = Path.Combine(directory, "audit.jsonl");
            var store = new AuditLogStore(path);

            Assert.True(store.Append(
                AuditEventType.ConnectionAccepted,
                "电脑一"));
            File.AppendAllText(path, "{ broken" + Environment.NewLine);
            Assert.True(store.Append(
                AuditEventType.ConnectionRejected,
                "电脑二"));

            var entries = store.ReadAll();

            Assert.Equal(2, entries.Count);
            Assert.Equal(AuditEventType.ConnectionAccepted, entries[0].Event);
            Assert.Equal(AuditEventType.ConnectionRejected, entries[1].Event);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Append_DoesNotRecordFileOrTextContent()
    {
        var directory = CreateTempDirectory();

        try
        {
            var path = Path.Combine(directory, "audit.jsonl");
            var store = new AuditLogStore(path);

            store.Append(
                AuditEventType.SensitiveFeatureEnabled,
                detail: "剪贴板同步");

            var json = File.ReadAllText(path);
            var entry = Assert.Single(store.ReadAll());

            Assert.Contains("SensitiveFeatureEnabled", json);
            Assert.Equal("剪贴板同步", entry.Detail);
            Assert.DoesNotContain("file-content", json);
            Assert.DoesNotContain("text-body", json);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "wdt-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

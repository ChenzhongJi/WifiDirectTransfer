using System.Text;
using System.Text.Json;
using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class TransferHistoryStoreTests
{
    [Fact]
    public void Add_PersistsAndReloadsNewestFirst()
    {
        InTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "history.json");
            var store = new TransferHistoryStore(path);

            store.Add(CreateEntry("first.bin", new DateTimeOffset(
                2026, 1, 1, 10, 0, 0, TimeSpan.FromHours(8))));
            store.Add(CreateEntry("second.bin", new DateTimeOffset(
                2026, 1, 1, 11, 0, 0, TimeSpan.FromHours(8))));

            var reloaded = new TransferHistoryStore(path).Load();

            Assert.Equal(2, reloaded.Count);
            Assert.Equal("second.bin", reloaded[0].FileName);
            Assert.Equal("first.bin", reloaded[1].FileName);
            Assert.Equal(TransferDirection.Receive, reloaded[0].Direction);
            Assert.Equal(TransferOutcome.Completed, reloaded[0].Outcome);
            Assert.Equal("PEER", reloaded[0].PeerName);
            Assert.Equal(@"C:\received\second.bin", reloaded[0].FilePath);
        });
    }

    [Fact]
    public void Load_TrimsToMaxEntries_NewestKept()
    {
        InTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "history.json");
            var entries = new List<TransferHistoryEntry>();

            for (var index = 0; index < TransferHistoryStore.MaxEntries + 5; index++)
            {
                entries.Add(CreateEntry(
                    $"file-{index}.bin",
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                        .AddSeconds(index)));
            }

            File.WriteAllText(
                path,
                JsonSerializer.Serialize(entries),
                new UTF8Encoding(false));

            var loaded = new TransferHistoryStore(path).Load();

            Assert.Equal(TransferHistoryStore.MaxEntries, loaded.Count);
            Assert.Equal(
                $"file-{TransferHistoryStore.MaxEntries + 4}.bin",
                loaded[0].FileName);
            Assert.DoesNotContain(loaded, item => item.FileName == "file-0.bin");
        });
    }

    [Fact]
    public void Load_CorruptFile_StartsEmptyAndCanBeReplaced()
    {
        InTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "history.json");
            File.WriteAllText(path, "not-json", new UTF8Encoding(false));

            var store = new TransferHistoryStore(path);
            Assert.Empty(store.Load());

            store.Add(CreateEntry("after-corrupt.bin", DateTimeOffset.Now));

            var reloaded = new TransferHistoryStore(path).Load();
            Assert.Single(reloaded);
            Assert.Equal("after-corrupt.bin", reloaded[0].FileName);
        });
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        InTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "history.json");
            var store = new TransferHistoryStore(path);
            store.Add(CreateEntry("a.bin", DateTimeOffset.Now));

            store.Clear();

            Assert.Empty(store.Load());
            Assert.Empty(new TransferHistoryStore(path).Load());
        });
    }

    [Fact]
    public void Add_CreatesMissingDirectory()
    {
        InTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "nested", "history.json");
            var store = new TransferHistoryStore(path);

            store.Add(CreateEntry("a.bin", DateTimeOffset.Now));

            Assert.True(File.Exists(path));
        });
    }

    private static TransferHistoryEntry CreateEntry(
        string fileName,
        DateTimeOffset occurredAt) =>
        new(
            occurredAt,
            TransferDirection.Receive,
            fileName,
            1234,
            TransferOutcome.Completed,
            "PEER",
            $@"C:\received\{fileName}");

    private static void InTempDirectory(Action<string> test)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "wdt-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            test(root);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // 测试清理忽略文件占用。
            }
        }
    }
}

using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class KnownDeviceStoreTests
{
    [Fact]
    public void DefaultPath_UsesLocalAppDataKnownDevicesFile()
    {
        var store = new KnownDeviceStore();

        Assert.Equal("known-devices.json", Path.GetFileName(store.FilePath));
        Assert.Equal(
            "WifiDirectTransfer",
            Path.GetFileName(Path.GetDirectoryName(store.FilePath)));
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsAndSortsNewestFirst()
    {
        var directory = CreateTempDirectory();

        try
        {
            var path = Path.Combine(directory, "known-devices.json");
            var store = new KnownDeviceStore(path);
            var older = DateTimeOffset.Now.AddHours(-2);
            var newer = DateTimeOffset.Now.AddMinutes(-5);

            Assert.True(store.Save(new[]
            {
                new KnownDevice
                {
                    Id = "peer-1",
                    DisplayName = "电脑一",
                    LastConnectedAt = older
                },
                new KnownDevice
                {
                    Id = "peer-2",
                    DisplayName = "电脑二",
                    LastConnectedAt = newer
                },
                new KnownDevice
                {
                    Id = "peer-1",
                    DisplayName = "电脑一（新）",
                    LastConnectedAt = newer
                }
            }));

            var loaded = await Task.Run(store.Load);

            Assert.Equal(2, loaded.Count);
            Assert.Equal("peer-1", loaded[0].Id);
            Assert.Equal("电脑一（新）", loaded[0].DisplayName);
            Assert.Equal("peer-2", loaded[1].Id);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Load_CorruptFile_ReturnsEmpty()
    {
        var directory = CreateTempDirectory();

        try
        {
            var path = Path.Combine(directory, "known-devices.json");
            await File.WriteAllTextAsync(path, "{ not valid json");

            var store = new KnownDeviceStore(path);

            Assert.Empty(store.Load());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WifiDirectTransfer.Tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试清理失败不应覆盖断言结果。
        }
        catch (UnauthorizedAccessException)
        {
            // 测试清理失败不应覆盖断言结果。
        }
    }
}

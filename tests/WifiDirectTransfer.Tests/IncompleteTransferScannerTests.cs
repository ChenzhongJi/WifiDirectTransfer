using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class IncompleteTransferScannerTests
{
    [Fact]
    public Task Scan_FindsPartAndMeta_AndListsOrphanPart()
    {
        return InTempDirectoryAsync(async directory =>
        {
            var metaBytes = TransferMetadataCodec.Serialize(
                new TransferMetadata("video.mp4", 1_000_000, 600_000));

            await File.WriteAllBytesAsync(
                Path.Combine(directory, "video.mp4.part"),
                new byte[600_000]);
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "video.mp4.meta"),
                metaBytes);
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "orphan.part"),
                new byte[1234]);
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "complete.bin"),
                new byte[10]);
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "stale.meta"),
                metaBytes);

            var results = IncompleteTransferScanner.Scan(directory);

            Assert.Equal(2, results.Count);

            var withMeta = Assert.Single(
                results,
                item => item.FileName == "video.mp4");
            Assert.Equal(1_000_000, withMeta.ExpectedBytes);
            Assert.Equal(600_000, withMeta.ReceivedBytes);
            Assert.NotNull(withMeta.MetaPath);

            var orphan = Assert.Single(
                results,
                item => item.FileName == "orphan");
            Assert.Null(orphan.ExpectedBytes);
            Assert.Equal(1234, orphan.ReceivedBytes);
            Assert.Null(orphan.MetaPath);
        });
    }

    [Fact]
    public Task Scan_FindsNestedPartAndMeta()
    {
        return InTempDirectoryAsync(async directory =>
        {
            var nested = Path.Combine(directory, "docs", "2026");
            Directory.CreateDirectory(nested);

            await File.WriteAllBytesAsync(
                Path.Combine(nested, "video.mp4.part"),
                new byte[100]);
            await File.WriteAllBytesAsync(
                Path.Combine(nested, "video.mp4.meta"),
                TransferMetadataCodec.Serialize(
                    new TransferMetadata("video.mp4", 200, 100)));

            var results = IncompleteTransferScanner.Scan(directory);

            var item = Assert.Single(results);
            Assert.Equal(
                Path.Combine("docs", "2026", "video.mp4"),
                item.FileName);
            Assert.Equal(200, item.ExpectedBytes);
            Assert.Equal(100, item.ReceivedBytes);
        });
    }

    [Fact]
    public Task Delete_RemovesPartAndMeta_AndKeepsCompleteFiles()
    {
        return InTempDirectoryAsync(async directory =>
        {
            var partPath = Path.Combine(directory, "doc.pdf.part");
            var metaPath = Path.Combine(directory, "doc.pdf.meta");
            var completePath = Path.Combine(directory, "complete.bin");

            await File.WriteAllBytesAsync(partPath, new byte[100]);
            await File.WriteAllBytesAsync(
                metaPath,
                TransferMetadataCodec.Serialize(
                    new TransferMetadata("doc.pdf", 200, 100)));
            await File.WriteAllBytesAsync(completePath, new byte[10]);

            var results = IncompleteTransferScanner.Scan(directory);
            Assert.Single(results);

            IncompleteTransferScanner.Delete(results);

            Assert.False(File.Exists(partPath));
            Assert.False(File.Exists(metaPath));
            Assert.True(File.Exists(completePath));
        });
    }

    [Fact]
    public void Scan_MissingDirectory_ReturnsEmpty()
    {
        var results = IncompleteTransferScanner.Scan(
            Path.Combine(
                Path.GetTempPath(),
                "wdt-missing-" + Guid.NewGuid().ToString("N")));

        Assert.Empty(results);
    }

    private static async Task InTempDirectoryAsync(Func<string, Task> test)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "wdt-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await test(root);
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

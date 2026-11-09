using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class DroppedFileCollectorTests
{
    [Fact]
    public async Task Collect_ExpandsFoldersAndDeduplicatesFiles()
    {
        var directory = CreateTempDirectory();

        try
        {
            var directPath = Path.Combine(directory, "direct.txt");
            var nestedDirectory = Path.Combine(directory, "nested");
            var nestedPath = Path.Combine(nestedDirectory, "child.txt");

            Directory.CreateDirectory(nestedDirectory);
            await File.WriteAllTextAsync(directPath, "direct");
            await File.WriteAllTextAsync(nestedPath, "child");

            var files = DroppedFileCollector.Collect(new[]
            {
                directory,
                directPath,
                nestedPath
            });

            Assert.Equal(2, files.Count);
            Assert.Contains(directPath, files);
            Assert.Contains(nestedPath, files);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CollectItems_PreservesFolderStructure()
    {
        var directory = CreateTempDirectory();

        try
        {
            var photos = Path.Combine(directory, "photos");
            var nested = Path.Combine(photos, "2026");
            var first = Path.Combine(nested, "a.jpg");
            var second = Path.Combine(nested, "b.jpg");

            Directory.CreateDirectory(nested);
            await File.WriteAllTextAsync(first, "a");
            await File.WriteAllTextAsync(second, "b");

            var items = DroppedFileCollector.CollectItems(new[] { photos });

            Assert.Equal(2, items.Count);
            Assert.All(
                items,
                item => Assert.Equal(
                    Path.Combine("photos", "2026"),
                    item.RelativeDirectory));
            Assert.Contains(items, item => item.FileName == "a.jpg");
            Assert.Contains(items, item => item.FileName == "b.jpg");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CollectItems_RenamesDuplicateRootFolders()
    {
        var directory = CreateTempDirectory();

        try
        {
            var firstRoot = Path.Combine(directory, "first", "docs");
            var secondRoot = Path.Combine(directory, "second", "docs");
            Directory.CreateDirectory(firstRoot);
            Directory.CreateDirectory(secondRoot);
            await File.WriteAllTextAsync(Path.Combine(firstRoot, "a.txt"), "a");
            await File.WriteAllTextAsync(Path.Combine(secondRoot, "b.txt"), "b");

            var items = DroppedFileCollector.CollectItems(
                new[] { firstRoot, secondRoot });

            Assert.Equal(2, items.Count);
            Assert.Contains(items, item => item.RelativeDirectory == "docs");
            Assert.Contains(items, item => item.RelativeDirectory == "docs (2)");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Collect_SkipsMissingPaths()
    {
        var directory = CreateTempDirectory();

        try
        {
            var messages = new List<string>();
            var missingPath = Path.Combine(directory, "missing.txt");

            var files = DroppedFileCollector.Collect(
                new[] { missingPath },
                messages.Add);

            Assert.Empty(files);
            Assert.Contains(
                messages,
                message => message.Contains("不存在", StringComparison.Ordinal));
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

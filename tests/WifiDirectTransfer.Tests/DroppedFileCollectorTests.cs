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

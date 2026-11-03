using System.IO.Compression;
using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class DiagnosticsPackageTests
{
    [Fact]
    public void Create_IncludesInfoAndExistingFiles()
    {
        InTempDirectory(directory =>
        {
            var appLog = Path.Combine(directory, "app.log");
            var startupLog = Path.Combine(directory, "startup.log");
            File.WriteAllText(appLog, "log-content");
            File.WriteAllText(startupLog, "startup-content");

            var zipPath = Path.Combine(directory, "diag.zip");

            DiagnosticsPackage.Create(
                zipPath,
                "info-content",
                new[]
                {
                    appLog,
                    startupLog,
                    Path.Combine(directory, "missing.log")
                });

            using var archive = ZipFile.OpenRead(zipPath);

            Assert.Equal(3, archive.Entries.Count);
            Assert.Equal("info-content", ReadEntry(archive, "info.txt"));
            Assert.Equal("log-content", ReadEntry(archive, "app.log"));
            Assert.Equal(
                "startup-content",
                ReadEntry(archive, "startup.log"));
        });
    }

    [Fact]
    public void Create_DuplicateFileNames_GetUniqueEntries()
    {
        InTempDirectory(directory =>
        {
            var firstDirectory = Path.Combine(directory, "first");
            var secondDirectory = Path.Combine(directory, "second");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);

            var firstLog = Path.Combine(firstDirectory, "app.log");
            var secondLog = Path.Combine(secondDirectory, "app.log");
            File.WriteAllText(firstLog, "first");
            File.WriteAllText(secondLog, "second");

            var zipPath = Path.Combine(directory, "diag.zip");
            DiagnosticsPackage.Create(
                zipPath,
                "info",
                new[] { firstLog, secondLog });

            using var archive = ZipFile.OpenRead(zipPath);

            Assert.Equal(3, archive.Entries.Count);
            Assert.Equal("first", ReadEntry(archive, "app.log"));
            Assert.Equal("second", ReadEntry(archive, "app-1.log"));
        });
    }

    [Fact]
    public void Create_OverwritesExistingFile()
    {
        InTempDirectory(directory =>
        {
            var zipPath = Path.Combine(directory, "diag.zip");
            File.WriteAllText(zipPath, "stale");

            DiagnosticsPackage.Create(
                zipPath,
                "fresh",
                Array.Empty<string>());

            using var archive = ZipFile.OpenRead(zipPath);

            Assert.Single(archive.Entries);
            Assert.Equal("fresh", ReadEntry(archive, "info.txt"));
        });
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);

        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static void InTempDirectory(Action<string> test)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "wdt-diag-" + Guid.NewGuid().ToString("N"));
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

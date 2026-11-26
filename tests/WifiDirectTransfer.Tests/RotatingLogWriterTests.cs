using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class RotatingLogWriterTests
{
    [Fact]
    public void Write_BelowLimit_AppendsAndReusesOpenWriter()
    {
        InTempDirectory(directory =>
        {
            var previousPath = Path.Combine(directory, "app.prev.log");
            using var writer = new RotatingLogWriter(
                Path.Combine(directory, "app.log"),
                maxFileBytes: 1024,
                previousFilePath: previousPath);

            writer.Write("first");
            writer.Write("second");

            Assert.True(writer.IsOpen);
            Assert.Equal(
                new[] { "first", "second" },
                ReadAllLinesShared(writer.FilePath));
            Assert.False(File.Exists(previousPath));
        });
    }

    [Fact]
    public void Write_ExceedingLimit_RotatesFileAndRecreatesItOnNextWrite()
    {
        InTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "app.log");
            var previousPath = Path.Combine(directory, "app.prev.log");
            using var writer = new RotatingLogWriter(
                path,
                maxFileBytes: 14,
                previousFilePath: previousPath);

            writer.Write("line-1");
            writer.Write("line-2");

            Assert.False(writer.IsOpen);
            Assert.Equal(new[] { "line-1", "line-2" }, ReadAllLinesShared(previousPath));
            Assert.False(File.Exists(path));

            writer.Write("line-3");

            Assert.True(writer.IsOpen);
            Assert.Equal(new[] { "line-3" }, ReadAllLinesShared(path));
            Assert.Equal(new[] { "line-1", "line-2" }, ReadAllLinesShared(previousPath));
        });
    }

    [Fact]
    public void Write_SecondRotation_ReplacesPreviousFile()
    {
        InTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "app.log");
            var previousPath = Path.Combine(directory, "app.prev.log");
            using var writer = new RotatingLogWriter(
                path,
                maxFileBytes: 14,
                previousFilePath: previousPath);

            writer.Write("line-1");
            writer.Write("line-2");
            writer.Write("line-3");
            writer.Write("line-4");

            Assert.Equal(new[] { "line-3", "line-4" }, ReadAllLinesShared(previousPath));
            Assert.False(File.Exists(path));
        });
    }

    [Fact]
    public void Write_ToUnwritableDirectory_IsSilentlyIgnored()
    {
        InTempDirectory(directory =>
        {
            var unwritablePath = Path.Combine(
                directory,
                "missing-parent-file",
                "app.log");
            File.WriteAllText(
                Path.Combine(directory, "missing-parent-file"),
                "blocker");

            using var writer = new RotatingLogWriter(
                unwritablePath,
                maxFileBytes: 1024);

            writer.Write("should not throw");
            Assert.False(writer.IsOpen);
        });
    }

    /// <summary>日志 writer 持有文件时，普通 File.ReadAllLines 会因共享模式冲突失败。</summary>
    private static string[] ReadAllLinesShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();

        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }

    private static void InTempDirectory(Action<string> test)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "wdt-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            test(directory);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // 测试清理忽略文件占用。
            }
        }
    }
}

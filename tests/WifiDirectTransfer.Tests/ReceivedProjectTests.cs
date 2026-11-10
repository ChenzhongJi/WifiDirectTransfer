using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class ReceivedProjectTests
{
    [Fact]
    public void BeginAndCompleteFolderFile_AggregatesProject()
    {
        var receiveDirectory = CreateTempDirectory();

        try
        {
            var collection = new ReceivedProjectCollection(
                () => receiveDirectory);
            var at = DateTimeOffset.Now;

            var project = collection.BeginFile(
                new FileTransferStartInfo(
                    "a.txt",
                    100,
                    0,
                    "docs/2026"),
                at);

            Assert.Single(collection.Projects);
            Assert.True(project.IsFolder);
            Assert.Equal("docs", project.DisplayName);
            Assert.Equal("docs", project.RootRelativePath);
            Assert.Equal(0, project.FileCount);
            Assert.True(project.IsReceiving);
            Assert.Equal("接收中", project.StatusText);

            collection.CompleteFile(
                new FileReceiveResult(
                    "a.txt",
                    Path.Combine(receiveDirectory, "docs", "2026", "a.txt"),
                    100,
                    new byte[32],
                    "docs/2026"),
                at.AddSeconds(1));

            Assert.Equal(1, project.FileCount);
            Assert.Equal(100, project.Size);
            Assert.False(project.IsReceiving);
            Assert.Equal("已接收", project.StatusText);
            Assert.Contains("1 个文件", project.Subtitle);

            collection.BeginFile(
                new FileTransferStartInfo(
                    "b.txt",
                    50,
                    0,
                    "docs/2026"),
                at.AddSeconds(2));
            collection.CompleteFile(
                new FileReceiveResult(
                    "b.txt",
                    Path.Combine(receiveDirectory, "docs", "2026", "b.txt"),
                    50,
                    new byte[32],
                    "docs/2026"),
                at.AddSeconds(3));

            Assert.Single(collection.Projects);
            Assert.Equal(2, project.FileCount);
            Assert.Equal(150, project.Size);
        }
        finally
        {
            DeleteDirectory(receiveDirectory);
        }
    }

    [Fact]
    public void DirectFiles_CreateSeparateProjects()
    {
        var receiveDirectory = CreateTempDirectory();

        try
        {
            var collection = new ReceivedProjectCollection(
                () => receiveDirectory);
            var at = DateTimeOffset.Now;

            collection.BeginFile(
                new FileTransferStartInfo("a.txt", 10, 0),
                at);
            collection.CompleteFile(
                new FileReceiveResult(
                    "a.txt",
                    Path.Combine(receiveDirectory, "a.txt"),
                    10,
                    new byte[32]),
                at);

            collection.BeginFile(
                new FileTransferStartInfo("b.txt", 20, 0),
                at);
            collection.CompleteFile(
                new FileReceiveResult(
                    "b.txt",
                    Path.Combine(receiveDirectory, "b.txt"),
                    20,
                    new byte[32]),
                at);

            Assert.Equal(2, collection.Projects.Count);
            Assert.All(
                collection.Projects,
                project => Assert.False(project.IsFolder));
            Assert.Contains(
                collection.Projects,
                project => project.DisplayName == "a.txt");
            Assert.Contains(
                collection.Projects,
                project => project.DisplayName == "b.txt");
        }
        finally
        {
            DeleteDirectory(receiveDirectory);
        }
    }

    [Fact]
    public void EmptyDirectories_CreateAndUpdateFolderProject()
    {
        var receiveDirectory = CreateTempDirectory();

        try
        {
            var collection = new ReceivedProjectCollection(
                () => receiveDirectory);
            var at = DateTimeOffset.Now;

            collection.AddEmptyDirectories(
                new[]
                {
                    "docs/empty",
                    "docs/empty2",
                    "docs/empty"
                },
                at);

            var project = Assert.Single(collection.Projects);

            Assert.True(project.IsFolder);
            Assert.Equal("docs", project.DisplayName);
            Assert.Equal(0, project.FileCount);
            Assert.Equal(2, project.EmptyDirectoryCount);
            Assert.Equal("空文件夹已创建", project.StatusText);
            Assert.Equal(
                Path.Combine(receiveDirectory, "docs"),
                project.FolderPath,
                ignoreCase: true);

            collection.BeginFile(
                new FileTransferStartInfo(
                    "a.txt",
                    10,
                    0,
                    "docs"),
                at.AddSeconds(1));
            collection.CompleteFile(
                new FileReceiveResult(
                    "a.txt",
                    Path.Combine(receiveDirectory, "docs", "a.txt"),
                    10,
                    new byte[32],
                    "docs"),
                at.AddSeconds(2));

            Assert.Same(project, Assert.Single(collection.Projects));
            Assert.Equal(1, project.FileCount);
            Assert.Equal(2, project.EmptyDirectoryCount);
            Assert.Equal("已接收", project.StatusText);
        }
        finally
        {
            DeleteDirectory(receiveDirectory);
        }
    }

    [Fact]
    public void DuplicateTopLevelRoots_CreateSeparateProjects()
    {
        var receiveDirectory = CreateTempDirectory();

        try
        {
            var collection = new ReceivedProjectCollection(
                () => receiveDirectory);
            var at = DateTimeOffset.Now;

            collection.AddEmptyDirectories(
                new[]
                {
                    "docs",
                    "docs (2)"
                },
                at);

            Assert.Equal(2, collection.Projects.Count);
            Assert.Contains(
                collection.Projects,
                project => project.DisplayName == "docs");
            Assert.Contains(
                collection.Projects,
                project => project.DisplayName == "docs (2)");
        }
        finally
        {
            DeleteDirectory(receiveDirectory);
        }
    }

    [Fact]
    public void FailActive_RemovesEmptyPendingProject()
    {
        var receiveDirectory = CreateTempDirectory();

        try
        {
            var collection = new ReceivedProjectCollection(
                () => receiveDirectory);

            collection.BeginFile(
                new FileTransferStartInfo("a.txt", 10, 0),
                DateTimeOffset.Now);
            collection.FailActive(DateTimeOffset.Now);

            Assert.Empty(collection.Projects);
        }
        finally
        {
            DeleteDirectory(receiveDirectory);
        }
    }

    [Fact]
    public void RefreshAvailability_DetectsMissingFolder()
    {
        var receiveDirectory = CreateTempDirectory();

        try
        {
            var folder = Path.Combine(receiveDirectory, "docs");
            Directory.CreateDirectory(folder);

            var collection = new ReceivedProjectCollection(
                () => receiveDirectory);
            collection.AddEmptyDirectories(new[] { "docs" }, DateTimeOffset.Now);
            var project = Assert.Single(collection.Projects);

            Assert.True(project.RefreshAvailability());
            Assert.False(project.IsMissing);

            Directory.Delete(folder);

            Assert.False(project.RefreshAvailability());
            Assert.True(project.IsMissing);
        }
        finally
        {
            DeleteDirectory(receiveDirectory);
        }
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("docs", "docs")]
    [InlineData("docs/2026", "docs")]
    [InlineData("docs\\2026", "docs")]
    public void GetTopLevelRoot_ReturnsFirstSegment(
        string relativePath,
        string expected)
    {
        Assert.Equal(
            expected,
            ReceivedProjectCollection.GetTopLevelRoot(relativePath));
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

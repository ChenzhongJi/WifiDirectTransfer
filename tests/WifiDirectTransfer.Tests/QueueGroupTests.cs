using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class QueueGroupTests
{
    [Fact]
    public Task EnqueuePlan_CreatesTopLevelGroups()
    {
        return InTempDirectoryAsync(async root =>
        {
            var folderA = Path.Combine(root, "source", "folderA");
            var folderB = Path.Combine(root, "source", "folderB");
            var directFile = Path.Combine(root, "source", "direct.txt");
            Directory.CreateDirectory(Path.Combine(folderA, "empty"));
            Directory.CreateDirectory(folderB);
            await File.WriteAllTextAsync(Path.Combine(folderA, "a1.txt"), "a1");
            await File.WriteAllTextAsync(Path.Combine(folderA, "a2.txt"), "a2");
            await File.WriteAllTextAsync(Path.Combine(folderB, "b.txt"), "b");
            await File.WriteAllTextAsync(directFile, "direct");

            var plan = DroppedFileCollector.CollectPlan(
                new[] { folderA, folderB, directFile });
            var queue = new TransferQueueManager(
                new MessageDispatcher(Stream.Null, Stream.Null));

            var result = queue.EnqueuePlan(plan);

            Assert.Equal(3, result.AddedGroups.Count);
            Assert.Equal(3, queue.Groups.Count);
            Assert.Equal(4, queue.Files.Count);
            Assert.True(queue.HasGroups);
            Assert.True(queue.CanStart);

            var first = queue.Groups[0];
            var second = queue.Groups[1];
            var third = queue.Groups[2];

            Assert.True(first.IsFolder);
            Assert.Equal("folderA", first.DisplayName);
            Assert.Equal(2, first.FileCount);
            Assert.Equal(1, first.EmptyDirectoryCount);
            Assert.Contains("2 个文件", first.Subtitle, StringComparison.Ordinal);
            Assert.Contains("1 个空文件夹", first.Subtitle, StringComparison.Ordinal);

            Assert.True(second.IsFolder);
            Assert.Equal("folderB", second.DisplayName);
            Assert.Equal(1, second.FileCount);
            Assert.Equal(0, second.EmptyDirectoryCount);

            Assert.False(third.IsFolder);
            Assert.Equal("direct.txt", third.DisplayName);
            Assert.Equal(1, third.FileCount);
        });
    }

    [Fact]
    public Task MoveGroup_ReordersTopLevelProjectsAndFiles()
    {
        return InTempDirectoryAsync(async root =>
        {
            var folderA = Path.Combine(root, "source", "folderA");
            var folderB = Path.Combine(root, "source", "folderB");
            Directory.CreateDirectory(folderA);
            Directory.CreateDirectory(folderB);
            await File.WriteAllTextAsync(Path.Combine(folderA, "a1.txt"), "a1");
            await File.WriteAllTextAsync(Path.Combine(folderA, "a2.txt"), "a2");
            await File.WriteAllTextAsync(Path.Combine(folderB, "b.txt"), "b");

            var plan = DroppedFileCollector.CollectPlan(
                new[] { folderA, folderB });
            var queue = new TransferQueueManager(
                new MessageDispatcher(Stream.Null, Stream.Null));
            queue.EnqueuePlan(plan);

            var first = queue.Groups[0];
            var second = queue.Groups[1];

            Assert.Equal(
                new[] { "a1.txt", "a2.txt", "b.txt" },
                queue.Files.Select(file => file.FileName));
            Assert.False(queue.CanMoveGroup(first, -1));
            Assert.True(queue.CanMoveGroup(second, -1));

            Assert.True(queue.MoveGroup(second, -1));

            Assert.Equal(
                new[] { second, first },
                queue.Groups);
            Assert.Equal(
                new[] { "b.txt", "a1.txt", "a2.txt" },
                queue.Files.Select(file => file.FileName));
            Assert.True(queue.CanMoveGroup(first, -1));
            Assert.True(queue.MoveGroup(first, -1));

            Assert.Equal(
                new[] { first, second },
                queue.Groups);
            Assert.Equal(
                new[] { "a1.txt", "a2.txt", "b.txt" },
                queue.Files.Select(file => file.FileName));
        });
    }

    [Fact]
    public Task RemoveGroup_RemovesAllWaitingFilesAndEmptyDirectories()
    {
        return InTempDirectoryAsync(async root =>
        {
            var folderA = Path.Combine(root, "source", "folderA");
            var folderB = Path.Combine(root, "source", "folderB");
            Directory.CreateDirectory(Path.Combine(folderA, "empty"));
            Directory.CreateDirectory(folderB);
            await File.WriteAllTextAsync(Path.Combine(folderA, "a1.txt"), "a1");
            await File.WriteAllTextAsync(Path.Combine(folderA, "a2.txt"), "a2");
            await File.WriteAllTextAsync(Path.Combine(folderB, "b.txt"), "b");

            var plan = DroppedFileCollector.CollectPlan(
                new[] { folderA, folderB });
            var queue = new TransferQueueManager(
                new MessageDispatcher(Stream.Null, Stream.Null));
            queue.EnqueuePlan(plan);

            var first = queue.Groups[0];
            var second = queue.Groups[1];

            Assert.True(queue.CanRemoveGroup(first));
            Assert.True(queue.RemoveGroup(first));

            Assert.Single(queue.Groups);
            Assert.Same(second, queue.Groups[0]);
            Assert.Equal(
                new[] { "b.txt" },
                queue.Files.Select(file => file.FileName));
            Assert.Equal(0, queue.EmptyDirectoryCount);

            Assert.True(queue.RemoveGroup(second));
            Assert.Empty(queue.Groups);
            Assert.Empty(queue.Files);
            Assert.False(queue.CanStart);
        });
    }

    [Fact]
    public Task Group_TracksAggregateStatusAndProgress()
    {
        return InTempDirectoryAsync(async root =>
        {
            var sourceRoot = Path.Combine(root, "source", "photos");
            var receiveRoot = Path.Combine(root, "receive");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(receiveRoot);
            await File.WriteAllBytesAsync(
                Path.Combine(sourceRoot, "a.bin"),
                CreatePayload(32 * 1024));
            await File.WriteAllBytesAsync(
                Path.Combine(sourceRoot, "b.bin"),
                CreatePayload(16 * 1024));

            var plan = DroppedFileCollector.CollectPlan(new[] { sourceRoot });

            await using var session = new TransferTestSession(receiveRoot);
            var queue = new TransferQueueManager(session.SenderDispatcher);
            var completed = new TaskCompletionSource<TransferQueueCompletedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            queue.QueueCompleted += (_, args) => completed.TrySetResult(args);
            queue.EnqueuePlan(plan);

            var group = Assert.Single(queue.Groups);

            Assert.Equal("等待中", group.StatusText);
            Assert.Equal("—", group.ProgressText);
            Assert.Equal(2, group.FileCount);
            Assert.Equal(3, group.Size / (16 * 1024)); // 32 KiB + 16 KiB

            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(20));
            await queue.StartAsync(cts.Token);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("已完成", group.StatusText);
            Assert.Equal(100d, group.ProgressPercent);
            Assert.Equal("100%", group.ProgressText);
            Assert.Equal(2, group.CompletedCount);
            Assert.Equal(0, group.FailedCount);
        });
    }

    [Fact]
    public Task EmptyDirectoryGroup_ShowsWaitingThenCreated()
    {
        return InTempDirectoryAsync(async root =>
        {
            var sourceRoot = Path.Combine(root, "source", "empty");
            var receiveRoot = Path.Combine(root, "receive");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(receiveRoot);

            var plan = DroppedFileCollector.CollectPlan(new[] { sourceRoot });

            await using var session = new TransferTestSession(receiveRoot);
            var queue = new TransferQueueManager(
                session.SenderDispatcher,
                supportsDirectoryManifest: true);
            var directoriesReceived = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            session.Receiver.DirectoriesReceived += (_, _) =>
                directoriesReceived.TrySetResult();
            queue.QueueCompleted += (_, _) =>
                completed.TrySetResult();
            queue.EnqueuePlan(plan);

            var group = Assert.Single(queue.Groups);

            Assert.True(group.IsFolder);
            Assert.Equal(0, group.FileCount);
            Assert.Equal(1, group.EmptyDirectoryCount);
            Assert.Equal("等待创建", group.StatusText);
            Assert.Equal("—", group.ProgressText);
            Assert.True(queue.CanStart);

            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(20));
            await queue.StartAsync(cts.Token);
            await directoriesReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(group.EmptyDirectoriesSent);
            Assert.Equal("已创建", group.StatusText);
            Assert.Equal(100d, group.ProgressPercent);
            Assert.Equal("100%", group.ProgressText);
            Assert.False(queue.CanStart);
        });
    }

    private static async Task InTempDirectoryAsync(
        Func<string, Task> test)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WifiDirectTransfer.Tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            await test(directory);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
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

    private static byte[] CreatePayload(int length)
    {
        var data = new byte[length];

        for (var index = 0; index < data.Length; index++)
        {
            data[index] = (byte)(((index * 37) + 11) % 256);
        }

        return data;
    }
}

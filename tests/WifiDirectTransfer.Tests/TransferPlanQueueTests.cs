using System.Buffers.Binary;
using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class TransferPlanQueueTests
{
    [Fact]
    public Task StartAsync_SendsDirectoryManifestBeforeFile()
    {
        return InTempDirectoryAsync(async root =>
        {
            var sourceRoot = Path.Combine(root, "source", "photos");
            var receiveRoot = Path.Combine(root, "receive");
            var emptyDirectory = Path.Combine(sourceRoot, "empty");
            Directory.CreateDirectory(emptyDirectory);
            Directory.CreateDirectory(receiveRoot);
            await File.WriteAllBytesAsync(
                Path.Combine(sourceRoot, "a.bin"),
                CreatePayload(32 * 1024));

            var plan = DroppedFileCollector.CollectPlan(new[] { sourceRoot });

            await using var session = new TransferTestSession(receiveRoot);
            var queue = new TransferQueueManager(
                session.SenderDispatcher,
                supportsDirectoryManifest: true);
            var events = new List<string>();
            var eventGate = new object();
            var directoriesReceived = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            session.Receiver.FileStarted += (_, _) =>
            {
                lock (eventGate)
                {
                    events.Add("file");
                }
            };

            session.Receiver.DirectoriesReceived += (_, args) =>
            {
                lock (eventGate)
                {
                    events.Add("directories");
                }

                directoriesReceived.TrySetResult();
            };

            var result = queue.EnqueuePlan(plan);

            Assert.Single(result.Files);
            Assert.Equal(1, result.EmptyDirectoryCount);
            Assert.Equal(1, queue.EmptyDirectoryCount);
            Assert.True(queue.CanStart);

            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(20));
            await queue.StartAsync(cts.Token);
            await directoriesReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            await session.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(
                Directory.Exists(
                    Path.Combine(receiveRoot, "photos", "empty")));

            lock (eventGate)
            {
                Assert.Equal(new[] { "directories", "file" }, events);
            }

            Assert.Equal(1, queue.CompletedCount);
            Assert.Contains(
                "空文件夹 1",
                queue.OverallProgressText,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public Task StartAsync_EmptyFolderOnly_CreatesDirectories()
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
            var completed = new TaskCompletionSource<TransferQueueCompletedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            session.Receiver.DirectoriesReceived += (_, _) =>
                directoriesReceived.TrySetResult();
            queue.QueueCompleted += (_, args) =>
                completed.TrySetResult(args);

            var result = queue.EnqueuePlan(plan);

            Assert.Empty(result.Files);
            Assert.Equal(1, result.EmptyDirectoryCount);
            Assert.Equal(0, queue.TotalCount);
            Assert.False(queue.HasFiles);
            Assert.True(queue.HasEmptyDirectories);
            Assert.True(queue.CanStart);
            Assert.Equal("将创建 1 个空文件夹", queue.OverallProgressText);

            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(20));
            await queue.StartAsync(cts.Token);
            await directoriesReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            var completion = await completed.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            Assert.True(
                Directory.Exists(Path.Combine(receiveRoot, "empty")));
            Assert.Equal(1, completion.EmptyDirectoryCount);
            Assert.Equal("已创建 1 个空文件夹", queue.OverallProgressText);
            Assert.Equal(100d, queue.OverallProgressPercent);
            Assert.False(queue.CanStart);
        });
    }

    [Fact]
    public Task EnqueuePlan_WhenPeerUnsupported_SkipsDirectories()
    {
        return InTempDirectoryAsync(async root =>
        {
            var sourceRoot = Path.Combine(root, "source", "photos");
            var receiveRoot = Path.Combine(root, "receive");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "empty"));
            Directory.CreateDirectory(receiveRoot);
            await File.WriteAllBytesAsync(
                Path.Combine(sourceRoot, "a.bin"),
                CreatePayload(4096));

            var plan = DroppedFileCollector.CollectPlan(new[] { sourceRoot });

            await using var session = new TransferTestSession(receiveRoot);
            var queue = new TransferQueueManager(
                session.SenderDispatcher,
                supportsDirectoryManifest: false);

            var result = queue.EnqueuePlan(plan);

            Assert.Single(result.Files);
            Assert.Equal(0, result.EmptyDirectoryCount);
            Assert.Equal(1, result.SkippedEmptyDirectoryCount);
            Assert.Equal(0, queue.EmptyDirectoryCount);
            Assert.Equal(1, queue.SkippedEmptyDirectoryCount);

            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(20));
            await queue.StartAsync(cts.Token);
            await session.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(
                Directory.Exists(
                    Path.Combine(receiveRoot, "photos", "empty")));
        });
    }

    [Fact]
    public Task StartAsync_SplitsLargeManifestAcrossMessages()
    {
        return InTempDirectoryAsync(async root =>
        {
            var output = new MemoryStream();
            var dispatcher = new MessageDispatcher(
                Stream.Null,
                output,
                maxPayloadLength: 32);
            var directories = Enumerable
                .Range(0, 12)
                .Select(index => $"empty{index:00}")
                .ToArray();
            var queue = new TransferQueueManager(
                dispatcher,
                supportsDirectoryManifest: true);

            queue.EnqueuePlan(
                new TransferPlan(
                    Array.Empty<TransferSource>(),
                    directories));

            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(10));
            await queue.StartAsync(cts.Token);

            var frames = TestFrameLog.Parse(output)
                .Where(frame => frame.Type == MessageType.ControlMessage)
                .ToArray();

            Assert.True(frames.Length > 1);

            var receivedDirectories = new List<string>();

            foreach (var frame in frames)
            {
                var message = ControlMessageCodec.Deserialize(frame.Payload);

                Assert.Equal(
                    ControlMessageSubtype.DirectoryManifest,
                    message.Subtype);
                receivedDirectories.AddRange(message.Directories!);
            }

            Assert.Equal(directories, receivedDirectories);
        });
    }

    [Fact]
    public Task DirectoryManifest_RejectsPathTraversal()
    {
        return InTempDirectoryAsync(async root =>
        {
            var receiveRoot = Path.Combine(root, "receive");
            Directory.CreateDirectory(receiveRoot);

            await using var session = new TransferTestSession(receiveRoot);
            var directoriesReceived =
                new TaskCompletionSource<DirectoryManifestEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            session.Receiver.DirectoriesReceived += (_, args) =>
                directoriesReceived.TrySetResult(args);

            await session.SenderDispatcher.SendAsync(
                MessageType.ControlMessage,
                ControlMessageCodec.SerializeDirectoryManifest(
                    new[] { "../escape" }),
                CancellationToken.None);

            var args = await directoriesReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            Assert.Equal(0, args.CreatedCount);
            Assert.Equal(1, args.FailedCount);
            Assert.False(
                Directory.Exists(
                    Path.Combine(root, "escape")));
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

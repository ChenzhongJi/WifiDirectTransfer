using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class BidirectionalTransferTests
{
    [Fact]
    public Task SimultaneousQueues_BothDirections_Complete()
    {
        return InTempDirectoryAsync(async root =>
        {
            var leftSource = Path.Combine(root, "left-source");
            var rightSource = Path.Combine(root, "right-source");
            var leftDestination = Path.Combine(root, "left-destination");
            var rightDestination = Path.Combine(root, "right-destination");

            Directory.CreateDirectory(leftSource);
            Directory.CreateDirectory(rightSource);
            Directory.CreateDirectory(leftDestination);
            Directory.CreateDirectory(rightDestination);

            var leftOutgoing = new Dictionary<string, byte[]>
            {
                ["left-a.bin"] = CreatePayload(128 * 1024),
                ["left-b.bin"] = CreatePayload(256 * 1024)
            };
            var rightOutgoing = new Dictionary<string, byte[]>
            {
                ["right-a.bin"] = CreatePayload(192 * 1024),
                ["right-b.bin"] = CreatePayload(64 * 1024)
            };

            var leftPaths = WriteFiles(leftSource, leftOutgoing);
            var rightPaths = WriteFiles(rightSource, rightOutgoing);

            await using var session = new BidirectionalTransferSession(
                leftDestination,
                rightDestination);

            var leftQueue = new TransferQueueManager(session.LeftDispatcher);
            var rightQueue = new TransferQueueManager(session.RightDispatcher);
            var leftCollector = new ReceiveCollector(
                session.LeftReceiver,
                rightOutgoing.Count);
            var rightCollector = new ReceiveCollector(
                session.RightReceiver,
                leftOutgoing.Count);

            leftQueue.EnqueueFiles(leftPaths);
            rightQueue.EnqueueFiles(rightPaths);

            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(20));

            var leftTask = leftQueue.StartAsync(cts.Token);
            var rightTask = rightQueue.StartAsync(cts.Token);

            await Task.WhenAll(leftTask, rightTask)
                .WaitAsync(TimeSpan.FromSeconds(15));

            var receivedAtLeft = await leftCollector.WaitAsync();
            var receivedAtRight = await rightCollector.WaitAsync();

            AssertReceived(rightOutgoing, receivedAtLeft);
            AssertReceived(leftOutgoing, receivedAtRight);

            Assert.Equal(2, leftQueue.CompletedCount);
            Assert.Equal(0, leftQueue.FailedCount);
            Assert.Equal(2, rightQueue.CompletedCount);
            Assert.Equal(0, rightQueue.FailedCount);
            Assert.All(
                leftQueue.Files,
                file => Assert.Equal(PendingFileStatus.Completed, file.Status));
            Assert.All(
                rightQueue.Files,
                file => Assert.Equal(PendingFileStatus.Completed, file.Status));

            Assert.Empty(Directory.GetFiles(leftDestination, "*.part"));
            Assert.Empty(Directory.GetFiles(leftDestination, "*.meta"));
            Assert.Empty(Directory.GetFiles(rightDestination, "*.part"));
            Assert.Empty(Directory.GetFiles(rightDestination, "*.meta"));
        });
    }

    private static void AssertReceived(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyList<FileReceiveResult> received)
    {
        Assert.Equal(expected.Count, received.Count);

        foreach (var result in received)
        {
            Assert.True(expected.TryGetValue(result.FileName, out var data));
            Assert.Equal(data, File.ReadAllBytes(result.FilePath));
            Assert.Equal(data.Length, result.FileSize);
        }
    }

    private static List<string> WriteFiles(
        string directory,
        IReadOnlyDictionary<string, byte[]> payloads)
    {
        var paths = new List<string>(payloads.Count);

        foreach (var (fileName, data) in payloads)
        {
            var path = Path.Combine(directory, fileName);
            File.WriteAllBytes(path, data);
            paths.Add(path);
        }

        return paths;
    }

    private static byte[] CreatePayload(int length)
    {
        var data = new byte[length];

        for (var index = 0; index < data.Length; index++)
        {
            data[index] = (byte)(((index * 31) + 7) % 256);
        }

        return data;
    }

    private sealed class ReceiveCollector
    {
        private readonly object _gate = new();
        private readonly List<FileReceiveResult> _results = new();
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _expectedCount;

        public ReceiveCollector(FileReceiver receiver, int expectedCount)
        {
            _expectedCount = expectedCount;
            receiver.FileReceived += OnFileReceived;
            receiver.FileFailed += OnFileFailed;
        }

        public async Task<IReadOnlyList<FileReceiveResult>> WaitAsync()
        {
            await _completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            lock (_gate)
            {
                return _results.ToArray();
            }
        }

        private void OnFileReceived(object? sender, FileReceivedEventArgs args)
        {
            lock (_gate)
            {
                _results.Add(args.Result);

                if (_results.Count == _expectedCount)
                {
                    _completed.TrySetResult();
                }
            }
        }

        private void OnFileFailed(object? sender, FileFailedEventArgs args)
        {
            _completed.TrySetException(args.Error);
        }
    }

    private static async Task InTempDirectoryAsync(Func<string, Task> test)
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
}

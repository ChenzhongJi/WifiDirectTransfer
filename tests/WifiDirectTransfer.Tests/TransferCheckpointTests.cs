using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class TransferCheckpointTests
{
    [Fact]
    public Task Receive_InsufficientStorage_RejectsBeforeCreatingPart()
    {
        return InTempDirectoryAsync(async receiveDirectory =>
        {
            var data = CreatePayload(64 * 1024);
            const long available = 32 * 1024;

            await using var session = new TransferTestSession(
                receiveDirectory,
                availableFreeSpaceProvider: _ => available);

            var senderException = await Assert.ThrowsAsync<RemoteTransferException>(
                () => session.SendBytesAsync(data, "big.bin"));

            Assert.Equal(
                WifiDirectErrorCode.StorageInsufficient,
                senderException.ErrorCode);
            Assert.Contains("磁盘空间不足", senderException.Message);

            var receiverException = await session.Failed.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            var storage = Assert.IsType<InsufficientStorageException>(
                receiverException);

            Assert.Equal(data.Length, storage.RequiredBytes);
            Assert.Equal(available, storage.AvailableBytes);
            Assert.Equal(data.Length - available, storage.ShortfallBytes);
            Assert.StartsWith(
                "接收失败：磁盘空间不足，需要",
                storage.UserMessage);
            Assert.Contains("可用 0.03 MB", storage.UserMessage);
            Assert.Empty(Directory.GetFiles(receiveDirectory));
        });
    }

    [Fact]
    public Task Queue_FirstFile_UsesTotalQueueSizeForStorageCheck()
    {
        return InTempDirectoryAsync(async root =>
        {
            var sourceDirectory = Path.Combine(root, "source");
            var receiveDirectory = Path.Combine(root, "receive");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(receiveDirectory);

            var firstPath = Path.Combine(sourceDirectory, "first.bin");
            var secondPath = Path.Combine(sourceDirectory, "second.bin");
            var firstData = CreatePayload(1000);
            var secondData = CreatePayload(1000);
            File.WriteAllBytes(firstPath, firstData);
            File.WriteAllBytes(secondPath, secondData);

            await using var session = new TransferTestSession(
                receiveDirectory,
                availableFreeSpaceProvider: _ => 1500);

            var queue = new TransferQueueManager(session.SenderDispatcher);
            var completed = new TaskCompletionSource<TransferQueueCompletedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            queue.QueueCompleted += (_, args) => completed.TrySetResult(args);

            queue.EnqueueFiles(new[] { firstPath, secondPath });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await queue.StartAsync(cts.Token);

            var summary = await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var receiverException = await session.Failed.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            var storage = Assert.IsType<InsufficientStorageException>(
                receiverException);

            Assert.Equal(2000, storage.RequiredBytes);
            Assert.Equal(1500, storage.AvailableBytes);
            Assert.Equal(1, summary.FailedCount);
            Assert.Equal(1, summary.SkippedCount);
            Assert.False(summary.WasCanceled);

            Assert.Equal(PendingFileStatus.Failed, queue.Files[0].Status);
            Assert.Equal(PendingFileStatus.Skipped, queue.Files[1].Status);
            Assert.Contains(
                "传输已停止",
                queue.Files[1].ErrorMessage);
            Assert.Empty(Directory.GetFiles(receiveDirectory));
        });
    }

    [Fact]
    public Task Receive_SameNameAutoRename_AppendsNumber()
    {
        return InTempDirectoryAsync(async directory =>
        {
            var existingPath = Path.Combine(directory, "same.bin");
            var existingContent = CreatePayload(128);
            File.WriteAllBytes(existingPath, existingContent);

            var data = CreatePayload(4096);

            await using var session = new TransferTestSession(directory);

            var sendResult = await session.SendBytesAsync(data, "same.bin");
            var received = await session.Received.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            Assert.Equal("same (1).bin", received.FileName);
            Assert.Equal(
                Path.Combine(directory, "same (1).bin"),
                received.FilePath);
            Assert.Equal(data, File.ReadAllBytes(received.FilePath));
            Assert.Equal(existingContent, File.ReadAllBytes(existingPath));
            Assert.Equal(data.Length, sendResult.FileSize);
            Assert.Empty(Directory.GetFiles(directory, "*.part"));
            Assert.Empty(Directory.GetFiles(directory, "*.meta"));
        });
    }

    [Fact]
    public Task Receive_SameNameOverwrite_ReplacesExistingFile()
    {
        return InTempDirectoryAsync(async directory =>
        {
            var existingPath = Path.Combine(directory, "same.bin");
            File.WriteAllBytes(existingPath, CreatePayload(128));

            var data = CreatePayload(4096);

            await using var session = new TransferTestSession(
                directory,
                sameNamePolicy: SameNamePolicy.Overwrite);

            await session.SendBytesAsync(data, "same.bin");
            var received = await session.Received.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            Assert.Equal("same.bin", received.FileName);
            Assert.Equal(existingPath, received.FilePath);
            Assert.Equal(data, File.ReadAllBytes(existingPath));
            Assert.Empty(Directory.GetFiles(directory, "*.part"));
            Assert.Empty(Directory.GetFiles(directory, "*.meta"));
        });
    }

    [Fact]
    public Task Receive_SameNameSkip_ReturnsStatusWithoutModifyingFile()
    {
        return InTempDirectoryAsync(async directory =>
        {
            var existingPath = Path.Combine(directory, "same.bin");
            var existingContent = CreatePayload(128);
            File.WriteAllBytes(existingPath, existingContent);

            await using var session = new TransferTestSession(
                directory,
                sameNamePolicy: SameNamePolicy.Skip);

            var exception = await Assert.ThrowsAsync<FileSkippedException>(
                () => session.SendBytesAsync(CreatePayload(4096), "same.bin"));

            var skipped = await session.Skipped.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            Assert.Equal("same.bin", skipped.FileName);
            Assert.Equal(WifiDirectErrorCode.FileSkipped, exception.ErrorCode);
            Assert.Equal(existingContent, File.ReadAllBytes(existingPath));
            Assert.Empty(Directory.GetFiles(directory, "*.part"));
            Assert.Empty(Directory.GetFiles(directory, "*.meta"));
        });
    }

    [Fact]
    public Task Queue_SkippedFile_DoesNotBlockNextFile()
    {
        return InTempDirectoryAsync(async root =>
        {
            var sourceDirectory = Path.Combine(root, "source");
            var receiveDirectory = Path.Combine(root, "receive");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(receiveDirectory);

            var conflictPath = Path.Combine(sourceDirectory, "conflict.bin");
            var otherPath = Path.Combine(sourceDirectory, "other.bin");
            var conflictData = CreatePayload(512);
            var otherData = CreatePayload(2048);
            File.WriteAllBytes(conflictPath, conflictData);
            File.WriteAllBytes(otherPath, otherData);

            var existingPath = Path.Combine(receiveDirectory, "conflict.bin");
            var existingContent = CreatePayload(64);
            File.WriteAllBytes(existingPath, existingContent);

            await using var session = new TransferTestSession(
                receiveDirectory,
                sameNamePolicy: SameNamePolicy.Skip);

            var queue = new TransferQueueManager(session.SenderDispatcher);
            var skipped = new TaskCompletionSource<QueueFileSkippedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            queue.FileSkipped += (_, args) => skipped.TrySetResult(args);

            var received = new List<FileReceiveResult>();
            var receivedGate = new object();
            var allReceived = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            session.Receiver.FileReceived += (_, args) =>
            {
                lock (receivedGate)
                {
                    received.Add(args.Result);

                    if (received.Count == 1)
                    {
                        allReceived.TrySetResult();
                    }
                }
            };

            session.Receiver.FileFailed += (_, args) =>
                allReceived.TrySetException(args.Error);

            queue.EnqueueFiles(new[] { conflictPath, otherPath });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await queue.StartAsync(cts.Token);
            await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var skippedEvent = await skipped.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            Assert.Equal("conflict.bin", skippedEvent.File.FileName);
            Assert.Equal(1, queue.CompletedCount);
            Assert.Equal(0, queue.FailedCount);
            Assert.Equal(1, queue.SkippedCount);
            Assert.Equal(PendingFileStatus.Skipped, queue.Files[0].Status);
            Assert.Equal(PendingFileStatus.Completed, queue.Files[1].Status);
            Assert.Equal(existingContent, File.ReadAllBytes(existingPath));

            lock (receivedGate)
            {
                var result = Assert.Single(received);
                Assert.Equal("other.bin", result.FileName);
                Assert.Equal(otherData, File.ReadAllBytes(result.FilePath));
            }
        });
    }

    [Fact]
    public void AckCongestionMonitor_WarnsAfterTwoConsecutiveSlowAcks()
    {
        var monitor = new AckCongestionMonitor();
        var slow = TimeSpan.FromSeconds(10.5);

        Assert.False(monitor.Report(slow));
        Assert.True(monitor.Report(slow));
        Assert.False(monitor.Report(slow));

        Assert.False(monitor.Report(TimeSpan.FromSeconds(1)));
        Assert.False(monitor.Report(slow));
        Assert.True(monitor.Report(slow));
        Assert.Equal("链路可能拥塞，传输速度可能下降", AckCongestionMonitor.WarningMessage);
    }

    [Fact]
    public async Task ReceiverPreferencesStore_RoundTripsAndFallsBackToDefault()
    {
        await InTempDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new ReceiverPreferencesStore(path);

            Assert.Equal(
                SameNamePolicy.AutoRename,
                store.Load().SameNamePolicy);

            Assert.True(
                store.Save(
                    new ReceiverPreferences
                    {
                        SameNamePolicy = SameNamePolicy.Skip
                    }));

            var reloaded = new ReceiverPreferencesStore(path).Load();
            Assert.Equal(SameNamePolicy.Skip, reloaded.SameNamePolicy);

            await File.WriteAllTextAsync(path, "{ not valid json");

            Assert.Equal(
                SameNamePolicy.AutoRename,
                store.Load().SameNamePolicy);
        });
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

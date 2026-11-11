using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class WindowedTransferTests
{
    [Fact]
    public Task WindowedQueue_TransfersMultiWindowFile()
    {
        return InTempDirectoryAsync(async root =>
        {
            var sourceDirectory = Path.Combine(root, "source");
            var destinationDirectory = Path.Combine(root, "destination");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(destinationDirectory);

            var payload = CreatePayload(8 * 1024 * 1024 + 123);
            var sourcePath = Path.Combine(sourceDirectory, "windowed.bin");
            File.WriteAllBytes(sourcePath, payload);

            await using var session = new BidirectionalTransferSession(
                Path.Combine(root, "left"),
                destinationDirectory);

            var logs = new List<string>();
            var received = new TaskCompletionSource<FileReceiveResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            session.RightReceiver.FileReceived += (_, args) =>
                received.TrySetResult(args.Result);
            session.RightReceiver.FileFailed += (_, args) =>
                received.TrySetException(args.Error);

            var queue = new TransferQueueManager(
                session.LeftDispatcher,
                log: message =>
                {
                    lock (logs)
                    {
                        logs.Add(message);
                    }
                },
                supportsWindowedTransfer: true);

            queue.EnqueueFiles(new[] { sourcePath });

            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(30));

            await queue.StartAsync(cts.Token)
                .WaitAsync(TimeSpan.FromSeconds(20));

            var result = await received.Task
                .WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(payload.Length, result.FileSize);
            Assert.Equal(payload, File.ReadAllBytes(result.FilePath));
            Assert.Equal(1, queue.CompletedCount);
            Assert.Equal(0, queue.FailedCount);

            lock (logs)
            {
                Assert.Contains(
                    logs,
                    message => message.Contains(
                        "启用窗口传输",
                        StringComparison.Ordinal));
                Assert.Contains(
                    logs,
                    message => message.Contains(
                        "窗口传输统计",
                        StringComparison.Ordinal));
            }
        });
    }

    [Fact]
    public Task WindowedQueue_TransfersSingleBlockFile()
    {
        return InTempDirectoryAsync(async root =>
        {
            var sourcePath = Path.Combine(root, "small.bin");
            var destinationDirectory = Path.Combine(root, "destination");
            Directory.CreateDirectory(destinationDirectory);

            var payload = CreatePayload(1234);
            File.WriteAllBytes(sourcePath, payload);

            await using var session = new BidirectionalTransferSession(
                Path.Combine(root, "left"),
                destinationDirectory);

            var received = new TaskCompletionSource<FileReceiveResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            session.RightReceiver.FileReceived += (_, args) =>
                received.TrySetResult(args.Result);
            session.RightReceiver.FileFailed += (_, args) =>
                received.TrySetException(args.Error);

            var queue = new TransferQueueManager(
                session.LeftDispatcher,
                supportsWindowedTransfer: true);
            queue.EnqueueFiles(new[] { sourcePath });

            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(20));

            await queue.StartAsync(cts.Token)
                .WaitAsync(TimeSpan.FromSeconds(15));

            var result = await received.Task
                .WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(payload, File.ReadAllBytes(result.FilePath));
        });
    }

    [Fact]
    public Task SimultaneousWindowedQueues_BothDirections_Complete()
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

            var leftPayload = CreatePayload(3 * 1024 * 1024 + 17);
            var rightPayload = CreatePayload(5 * 1024 * 1024 + 91);
            var leftPath = Path.Combine(leftSource, "left.bin");
            var rightPath = Path.Combine(rightSource, "right.bin");
            File.WriteAllBytes(leftPath, leftPayload);
            File.WriteAllBytes(rightPath, rightPayload);

            await using var session = new BidirectionalTransferSession(
                leftDestination,
                rightDestination);

            var receivedAtLeft = new TaskCompletionSource<FileReceiveResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var receivedAtRight = new TaskCompletionSource<FileReceiveResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            session.LeftReceiver.FileReceived += (_, args) =>
                receivedAtLeft.TrySetResult(args.Result);
            session.LeftReceiver.FileFailed += (_, args) =>
                receivedAtLeft.TrySetException(args.Error);
            session.RightReceiver.FileReceived += (_, args) =>
                receivedAtRight.TrySetResult(args.Result);
            session.RightReceiver.FileFailed += (_, args) =>
                receivedAtRight.TrySetException(args.Error);

            var leftQueue = new TransferQueueManager(
                session.LeftDispatcher,
                supportsWindowedTransfer: true);
            var rightQueue = new TransferQueueManager(
                session.RightDispatcher,
                supportsWindowedTransfer: true);

            leftQueue.EnqueueFiles(new[] { leftPath });
            rightQueue.EnqueueFiles(new[] { rightPath });

            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(30));

            await Task.WhenAll(
                    leftQueue.StartAsync(cts.Token),
                    rightQueue.StartAsync(cts.Token))
                .WaitAsync(TimeSpan.FromSeconds(20));

            var leftResult = await receivedAtLeft.Task
                .WaitAsync(TimeSpan.FromSeconds(20));
            var rightResult = await receivedAtRight.Task
                .WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(rightPayload, File.ReadAllBytes(leftResult.FilePath));
            Assert.Equal(leftPayload, File.ReadAllBytes(rightResult.FilePath));
            Assert.Equal(0, leftQueue.FailedCount);
            Assert.Equal(0, rightQueue.FailedCount);
        });
    }

    [Fact]
    public Task WindowedTransfer_ResumesFromPersistedOffset()
    {
        return InTempDirectoryAsync(async root =>
        {
            const string fileName = "resume-window.bin";
            const int resumeOffset = 512 * 1024;
            var payload = CreatePayload(2 * 1024 * 1024 + 321);
            var partPath = Path.Combine(root, fileName + ".part");
            var metaPath = Path.Combine(root, fileName + ".meta");

            File.WriteAllBytes(
                partPath,
                payload.AsSpan(0, resumeOffset).ToArray());
            File.WriteAllBytes(
                metaPath,
                TransferMetadataCodec.Serialize(
                    new TransferMetadata(
                        fileName,
                        payload.Length,
                        resumeOffset)));

            await using var session = new TransferTestSession(root);

            var sendResult = await session.SendBytesAsync(
                payload,
                fileName,
                windowedTransfer: true);

            var receiveResult = await session.Received.Task
                .WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(resumeOffset, sendResult.StartOffset);
            Assert.Equal(payload.Length, receiveResult.FileSize);
            Assert.Equal(payload, File.ReadAllBytes(receiveResult.FilePath));
            Assert.False(File.Exists(partPath));
            Assert.False(File.Exists(metaPath));
        });
    }

    [Fact]
    public async Task WindowedTransfer_RemoteError_AbortsSender()
    {
        await using var transport = new TestDuplexTransport();
        var sender = new MessageDispatcher(
            transport.SenderInput,
            transport.SenderOutput);
        var receiver = new MessageDispatcher(
            transport.ReceiverInput,
            transport.ReceiverOutput);

        var chunks = 0;

        receiver.MessageReceived += async (dispatcher, args, cancellationToken) =>
        {
            if (args.Type == MessageType.FileStart)
            {
                await dispatcher.SendAsync(
                    MessageType.ControlMessage,
                    ControlMessageCodec.SerializeResumeRequest(0),
                    cancellationToken);
            }
            else if (args.Type == MessageType.FileChunk)
            {
                chunks++;

                if (chunks == 5)
                {
                    await dispatcher.SendAsync(
                        MessageType.ControlMessage,
                        ControlMessageCodec.SerializeTransferError(
                            WifiDirectErrorCode.StorageInsufficient,
                            "接收端磁盘空间不足。"),
                        cancellationToken);
                }
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var senderLoop = sender.StartReceiveLoopAsync(cts.Token);
        var receiverLoop = receiver.StartReceiveLoopAsync(cts.Token);

        try
        {
            using var source = new MemoryStream(new byte[1024 * 1024]);

            var exception = await Assert.ThrowsAsync<RemoteTransferException>(
                () => FileSender.SendAsync(
                    sender,
                    source,
                    "window-error.bin",
                    windowedTransfer: true));

            Assert.Equal(
                WifiDirectErrorCode.StorageInsufficient,
                exception.ErrorCode);
            Assert.True(chunks >= 5);
        }
        finally
        {
            cts.Cancel();
            transport.CompleteBoth();

            try
            {
                await senderLoop;
            }
            catch (Exception)
            {
                // 测试清理忽略循环退出异常。
            }

            try
            {
                await receiverLoop;
            }
            catch (Exception)
            {
                // 测试清理忽略循环退出异常。
            }
        }
    }

    [Fact]
    public Task WindowedQueue_TransfersEmptyFile()
    {
        return InTempDirectoryAsync(async root =>
        {
            var sourcePath = Path.Combine(root, "empty.bin");
            var destinationDirectory = Path.Combine(root, "destination");
            Directory.CreateDirectory(destinationDirectory);
            File.WriteAllBytes(sourcePath, Array.Empty<byte>());

            await using var session = new BidirectionalTransferSession(
                Path.Combine(root, "left"),
                destinationDirectory);

            var received = new TaskCompletionSource<FileReceiveResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            session.RightReceiver.FileReceived += (_, args) =>
                received.TrySetResult(args.Result);
            session.RightReceiver.FileFailed += (_, args) =>
                received.TrySetException(args.Error);

            var queue = new TransferQueueManager(
                session.LeftDispatcher,
                supportsWindowedTransfer: true);
            queue.EnqueueFiles(new[] { sourcePath });

            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(20));

            await queue.StartAsync(cts.Token)
                .WaitAsync(TimeSpan.FromSeconds(15));

            var result = await received.Task
                .WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(0, result.FileSize);
            Assert.Empty(File.ReadAllBytes(result.FilePath));
        });
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

    private static async Task InTempDirectoryAsync(
        Func<string, Task> test)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "wdt-window-" + Guid.NewGuid().ToString("N"));
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

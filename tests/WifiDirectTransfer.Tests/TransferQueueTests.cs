using System.Security.Cryptography;
using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class TransferQueueTests
{
    [Fact]
    public Task StartAsync_SendsFilesSerially_AndReportsProgress()
    {
        return InTempDirectoryAsync(async root =>
        {
            var sourceDirectory = Path.Combine(root, "source");
            var receiveDirectory = Path.Combine(root, "receive");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(receiveDirectory);

            var payloads = new Dictionary<string, byte[]>
            {
                ["a.bin"] = CreatePayload(100_000),
                ["b.bin"] = CreatePayload(200_000),
                ["c.bin"] = CreatePayload(50_000)
            };

            var paths = WriteSources(sourceDirectory, payloads);

            await using var session = new TransferTestSession(receiveDirectory);
            var queue = new TransferQueueManager(session.SenderDispatcher);
            var events = new List<string>();
            var eventsGate = new object();

            queue.FileStarted += (_, args) =>
            {
                lock (eventsGate)
                {
                    events.Add($"start:{args.File.FileName}:{args.StartInfo.StartOffset}");
                }
            };

            queue.FileCompleted += (_, args) =>
            {
                lock (eventsGate)
                {
                    events.Add($"complete:{args.File.FileName}");
                }
            };

            queue.FileFailed += (_, args) =>
            {
                lock (eventsGate)
                {
                    events.Add($"failed:{args.File.FileName}");
                }
            };

            var received = new List<FileReceiveResult>();
            var receivedGate = new object();
            var allReceived = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            session.Receiver.FileReceived += (_, args) =>
            {
                lock (receivedGate)
                {
                    received.Add(args.Result);

                    if (received.Count == paths.Count)
                    {
                        allReceived.TrySetResult();
                    }
                }
            };

            session.Receiver.FileFailed += (_, args) =>
                allReceived.TrySetException(args.Error);

            Assert.Equal("尚未添加文件", queue.OverallProgressText);

            queue.EnqueueFiles(paths);

            Assert.Equal(3, queue.TotalCount);
            Assert.True(queue.HasFiles);
            Assert.True(queue.CanStart);
            Assert.Equal("第 0 / 3 个文件", queue.CurrentPositionText);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await queue.StartAsync(cts.Token);
            await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(3, queue.CompletedCount);
            Assert.Equal(0, queue.FailedCount);
            Assert.Equal(0, queue.SkippedCount);
            Assert.Equal(3, queue.FinishedCount);
            Assert.Equal(100d, queue.OverallProgressPercent);
            Assert.Equal(3, queue.CurrentIndex);
            Assert.Same(queue.Files[2], queue.CurrentFile);
            Assert.Equal("第 3 / 3 个文件", queue.CurrentPositionText);
            Assert.Equal("已完成 3 / 3", queue.OverallProgressText);
            Assert.False(queue.IsRunning);
            Assert.False(queue.HasWaitingFiles);
            Assert.False(queue.CanStart);

            foreach (var file in queue.Files)
            {
                Assert.Equal(PendingFileStatus.Completed, file.Status);
                Assert.Equal(payloads[file.FileName].Length, file.BytesTransferred);
                Assert.Null(file.ErrorMessage);
            }

            lock (eventsGate)
            {
                Assert.Equal(
                    new[]
                    {
                        "start:a.bin:0",
                        "complete:a.bin",
                        "start:b.bin:0",
                        "complete:b.bin",
                        "start:c.bin:0",
                        "complete:c.bin"
                    },
                    events);
            }

            lock (receivedGate)
            {
                Assert.Equal(paths.Count, received.Count);

                for (var index = 0; index < received.Count; index++)
                {
                    var result = received[index];
                    var expected = payloads[result.FileName];

                    Assert.Equal(expected.Length, result.FileSize);
                    Assert.Equal(SHA256.HashData(expected), result.Sha256);
                    Assert.Equal(expected, File.ReadAllBytes(result.FilePath));
                }
            }

            Assert.Empty(Directory.GetFiles(receiveDirectory, "*.part"));
            Assert.Empty(Directory.GetFiles(receiveDirectory, "*.meta"));
        });
    }

    [Fact]
    public Task StartAsync_FileAppendedWhileRunning_IsDrainedInSameRun()
    {
        return InTempDirectoryAsync(async root =>
        {
            var sourceDirectory = Path.Combine(root, "source");
            var receiveDirectory = Path.Combine(root, "receive");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(receiveDirectory);

            var firstData = CreatePayload(256 * 1024);
            var secondData = CreatePayload(256 * 1024);
            var firstPath = Path.Combine(sourceDirectory, "first.bin");
            var secondPath = Path.Combine(sourceDirectory, "second.bin");
            File.WriteAllBytes(firstPath, firstData);
            File.WriteAllBytes(secondPath, secondData);

            await using var session = new TransferTestSession(receiveDirectory);
            var queue = new TransferQueueManager(session.SenderDispatcher);

            var completed = new TaskCompletionSource<
                TransferQueueCompletedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            queue.QueueCompleted += (_, args) => completed.TrySetResult(args);

            var received = new List<FileReceiveResult>();
            var receivedGate = new object();
            var allReceived = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            session.Receiver.FileReceived += (_, args) =>
            {
                lock (receivedGate)
                {
                    received.Add(args.Result);

                    if (received.Count == 2)
                    {
                        allReceived.TrySetResult();
                    }
                }
            };

            session.Receiver.FileFailed += (_, args) =>
                allReceived.TrySetException(args.Error);

            queue.FileStarted += (_, args) =>
            {
                if (args.File.FileName == "first.bin")
                {
                    queue.EnqueueFiles(new[] { secondPath });
                }
            };

            queue.EnqueueFiles(new[] { firstPath });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await queue.StartAsync(cts.Token);

            var summary = await completed.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(2, summary.TotalCount);
            Assert.Equal(2, summary.CompletedCount);
            Assert.Equal(0, summary.FailedCount);
            Assert.Equal(0, summary.SkippedCount);
            Assert.Equal(2, queue.TotalCount);
            Assert.Equal(2, queue.CompletedCount);
            Assert.Equal(PendingFileStatus.Completed, queue.Files[0].Status);
            Assert.Equal(PendingFileStatus.Completed, queue.Files[1].Status);
            Assert.Equal(100d, queue.OverallProgressPercent);
            Assert.False(queue.IsRunning);

            lock (receivedGate)
            {
                Assert.Equal(2, received.Count);
                Assert.Equal(
                    firstData,
                    File.ReadAllBytes(received[0].FilePath));
                Assert.Equal(
                    secondData,
                    File.ReadAllBytes(received[1].FilePath));
            }

            Assert.Empty(Directory.GetFiles(receiveDirectory, "*.part"));
            Assert.Empty(Directory.GetFiles(receiveDirectory, "*.meta"));
        });
    }

    [Fact]
    public Task StartAsync_FailedFile_DoesNotBlockSubsequentFiles()
    {
        return InTempDirectoryAsync(async root =>
        {
            var sourceDirectory = Path.Combine(root, "source");
            var receiveDirectory = Path.Combine(root, "receive");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(receiveDirectory);

            var first = CreatePayload(96_000);
            var third = CreatePayload(128_000);
            var firstPath = Path.Combine(sourceDirectory, "first.bin");
            var missingPath = Path.Combine(sourceDirectory, "missing.bin");
            var thirdPath = Path.Combine(sourceDirectory, "third.bin");

            File.WriteAllBytes(firstPath, first);
            File.WriteAllBytes(thirdPath, third);

            await using var session = new TransferTestSession(receiveDirectory);
            var queue = new TransferQueueManager(session.SenderDispatcher);
            var received = new List<FileReceiveResult>();
            var receivedGate = new object();
            var allReceived = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            session.Receiver.FileReceived += (_, args) =>
            {
                lock (receivedGate)
                {
                    received.Add(args.Result);

                    if (received.Count == 2)
                    {
                        allReceived.TrySetResult();
                    }
                }
            };

            session.Receiver.FileFailed += (_, args) =>
                allReceived.TrySetException(args.Error);

            var failedFiles = new List<PendingFile>();
            queue.FileFailed += (_, args) => failedFiles.Add(args.File);

            queue.EnqueueFiles(new[] { firstPath, missingPath, thirdPath });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await queue.StartAsync(cts.Token);
            await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(2, queue.CompletedCount);
            Assert.Equal(1, queue.FailedCount);
            Assert.Equal(0, queue.SkippedCount);
            Assert.Equal(3, queue.FinishedCount);
            Assert.Equal(100d, queue.OverallProgressPercent);

            Assert.Equal(PendingFileStatus.Completed, queue.Files[0].Status);
            Assert.Equal(PendingFileStatus.Failed, queue.Files[1].Status);
            Assert.Equal(PendingFileStatus.Completed, queue.Files[2].Status);
            Assert.False(string.IsNullOrWhiteSpace(queue.Files[1].ErrorMessage));
            Assert.Same(queue.Files[1], Assert.Single(failedFiles));

            lock (receivedGate)
            {
                Assert.Equal(2, received.Count);
                Assert.Equal(first, File.ReadAllBytes(received[0].FilePath));
                Assert.Equal(third, File.ReadAllBytes(received[1].FilePath));
            }

            Assert.Empty(Directory.GetFiles(receiveDirectory, "*.part"));
            Assert.Empty(Directory.GetFiles(receiveDirectory, "*.meta"));
        });
    }

    [Fact]
    public Task StartAsync_ResumesEachFileIndependently_FromExistingMeta()
    {
        return InTempDirectoryAsync(async root =>
        {
            var sourceDirectory = Path.Combine(root, "source");
            var receiveDirectory = Path.Combine(root, "receive");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(receiveDirectory);

            var resumeData = CreatePayload(1024 * 1024);
            var otherData = CreatePayload(300_000);
            var resumePath = Path.Combine(sourceDirectory, "resume.bin");
            var otherPath = Path.Combine(sourceDirectory, "other.bin");

            File.WriteAllBytes(resumePath, resumeData);
            File.WriteAllBytes(otherPath, otherData);

            var partPath = Path.Combine(receiveDirectory, "resume.bin.part");
            var metaPath = Path.Combine(receiveDirectory, "resume.bin.meta");

            await using (var first = new TransferTestSession(receiveDirectory))
            {
                using var shortSource = new TruncatedReadStream(
                    resumeData.AsSpan(0, 512 * 1024).ToArray(),
                    resumeData.Length);

                var changed = await Assert.ThrowsAsync<FileChangedException>(
                    () => first.SendAsync(shortSource, "resume.bin"));

                Assert.Equal(resumeData.Length, changed.ExpectedLength);
                Assert.Equal(512 * 1024, changed.ActualLength);

                first.CompleteTransport();
                first.CancelSender();

                var failure = await first.Failed.Task.WaitAsync(
                    TimeSpan.FromSeconds(10));
                Assert.IsType<TransferTruncatedException>(failure);
            }

            Assert.True(File.Exists(partPath));
            Assert.True(File.Exists(metaPath));
            Assert.Equal(512 * 1024, new FileInfo(partPath).Length);

            await using var second = new TransferTestSession(receiveDirectory);
            var queue = new TransferQueueManager(second.SenderDispatcher);
            var senderStarts = new List<FileTransferStartInfo>();
            var receiverStarts = new List<FileTransferStartInfo>();
            var startsGate = new object();

            queue.FileStarted += (_, args) =>
            {
                lock (startsGate)
                {
                    senderStarts.Add(args.StartInfo);
                }
            };

            second.Receiver.FileStarted += (_, args) =>
            {
                lock (startsGate)
                {
                    receiverStarts.Add(args.StartInfo);
                }
            };

            var received = new List<FileReceiveResult>();
            var receivedGate = new object();
            var allReceived = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            second.Receiver.FileReceived += (_, args) =>
            {
                lock (receivedGate)
                {
                    received.Add(args.Result);

                    if (received.Count == 2)
                    {
                        allReceived.TrySetResult();
                    }
                }
            };

            second.Receiver.FileFailed += (_, args) =>
                allReceived.TrySetException(args.Error);

            queue.EnqueueFiles(new[] { resumePath, otherPath });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await queue.StartAsync(cts.Token);
            await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(2, queue.CompletedCount);
            Assert.Equal(0, queue.FailedCount);
            Assert.Equal(512 * 1024, queue.Files[0].StartOffset);
            Assert.True(queue.Files[0].IsResume);
            Assert.Equal(0, queue.Files[1].StartOffset);
            Assert.False(queue.Files[1].IsResume);

            lock (startsGate)
            {
                Assert.Equal(2, senderStarts.Count);
                Assert.Equal(512 * 1024, senderStarts[0].StartOffset);
                Assert.Equal(0, senderStarts[1].StartOffset);
                Assert.Equal(2, receiverStarts.Count);
                Assert.Equal(512 * 1024, receiverStarts[0].StartOffset);
                Assert.Equal(0, receiverStarts[1].StartOffset);
            }

            lock (receivedGate)
            {
                Assert.Equal(2, received.Count);
                Assert.Equal("resume.bin", received[0].FileName);
                Assert.Equal(resumeData, File.ReadAllBytes(received[0].FilePath));
                Assert.Equal(SHA256.HashData(resumeData), received[0].Sha256);
                Assert.Equal("other.bin", received[1].FileName);
                Assert.Equal(otherData, File.ReadAllBytes(received[1].FilePath));
            }

            Assert.False(File.Exists(partPath));
            Assert.False(File.Exists(metaPath));
            Assert.Empty(Directory.GetFiles(receiveDirectory, "*.part"));
            Assert.Empty(Directory.GetFiles(receiveDirectory, "*.meta"));
        });
    }

    [Fact]
    public async Task Clear_ResetsQueueState()
    {
        await InTempDirectoryAsync(root =>
        {
            var firstPath = Path.Combine(root, "first.bin");
            var secondPath = Path.Combine(root, "second.bin");

            File.WriteAllBytes(firstPath, CreatePayload(1024));
            File.WriteAllBytes(secondPath, CreatePayload(2048));

            var dispatcher = new MessageDispatcher(Stream.Null, Stream.Null);
            var queue = new TransferQueueManager(dispatcher);

            queue.EnqueueFiles(new[] { firstPath, secondPath });

            Assert.True(queue.CanStart);
            Assert.Equal(2, queue.TotalCount);

            queue.Clear();

            Assert.Empty(queue.Files);
            Assert.Equal(0, queue.TotalCount);
            Assert.Null(queue.CurrentFile);
            Assert.False(queue.HasFiles);
            Assert.False(queue.CanStart);
            Assert.Equal("尚未添加文件", queue.OverallProgressText);
            Assert.Equal("第 0 / 0 个文件", queue.CurrentPositionText);

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task StartAsync_PreCanceledToken_MarksAllFilesSkipped()
    {
        await InTempDirectoryAsync(async root =>
        {
            var firstPath = Path.Combine(root, "first.bin");
            var secondPath = Path.Combine(root, "second.bin");

            File.WriteAllBytes(firstPath, CreatePayload(1024));
            File.WriteAllBytes(secondPath, CreatePayload(2048));

            var dispatcher = new MessageDispatcher(Stream.Null, Stream.Null);
            var queue = new TransferQueueManager(dispatcher);
            var completed = new TaskCompletionSource<TransferQueueCompletedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            queue.QueueCompleted += (_, args) => completed.TrySetResult(args);
            queue.EnqueueFiles(new[] { firstPath, secondPath });

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await queue.StartAsync(cts.Token);

            var summary = await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(summary.WasCanceled);
            Assert.Equal(2, summary.TotalCount);
            Assert.Equal(0, summary.CompletedCount);
            Assert.Equal(0, summary.FailedCount);
            Assert.Equal(2, summary.SkippedCount);

            Assert.All(
                queue.Files,
                file =>
                {
                    Assert.Equal(PendingFileStatus.Skipped, file.Status);
                    Assert.False(string.IsNullOrWhiteSpace(file.ErrorMessage));
                });

            Assert.Equal(2, queue.SkippedCount);
            Assert.Equal(2, queue.FinishedCount);
            Assert.Equal(100d, queue.OverallProgressPercent);
            Assert.False(queue.IsRunning);
        });
    }

    private static List<string> WriteSources(
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

    private sealed class TruncatedReadStream : MemoryStream
    {
        private readonly long _reportedLength;

        public TruncatedReadStream(byte[] buffer, long reportedLength)
            : base(buffer)
        {
            _reportedLength = reportedLength;
        }

        public override long Length => _reportedLength;
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

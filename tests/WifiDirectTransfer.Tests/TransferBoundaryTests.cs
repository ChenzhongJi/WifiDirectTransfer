using System.Security.Cryptography;
using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class TransferBoundaryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(65_535)]
    [InlineData(65_536)]
    [InlineData(65_537)]
    [InlineData(131_073)]
    public Task SendReceive_ChunkBoundarySizes_RoundTrip(int size)
    {
        return InTempDirectoryAsync(async directory =>
        {
            var data = CreatePayload(size);

            await using var session = new TransferTestSession(directory);

            var sendResult = await session.SendBytesAsync(
                data,
                $"boundary-{size}.bin");
            var receiveResult = await session.Received.Task.WaitAsync(
                TimeSpan.FromSeconds(10));

            Assert.Equal(size, sendResult.FileSize);
            Assert.Equal(SHA256.HashData(data), receiveResult.Sha256);
            Assert.Equal(data, File.ReadAllBytes(receiveResult.FilePath));
            Assert.Empty(Directory.GetFiles(directory, "*.part"));
            Assert.Empty(Directory.GetFiles(directory, "*.meta"));
        });
    }

    [Fact]
    public Task Queue_300SmallFiles_AllComplete()
    {
        return InTempDirectoryAsync(async root =>
        {
            var sourceDirectory = Path.Combine(root, "source");
            var receiveDirectory = Path.Combine(root, "receive");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(receiveDirectory);

            var paths = new List<string>();

            for (var index = 0; index < 300; index++)
            {
                var path = Path.Combine(
                    sourceDirectory,
                    $"file-{index:0000}.bin");
                await File.WriteAllBytesAsync(
                    path,
                    CreatePayload(1024));
                paths.Add(path);
            }

            await using var session = new TransferTestSession(receiveDirectory);
            var queue = new TransferQueueManager(session.SenderDispatcher);

            var completed = new TaskCompletionSource<
                TransferQueueCompletedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            queue.QueueCompleted += (_, args) => completed.TrySetResult(args);

            var receivedCount = 0;
            var receivedGate = new object();
            var allReceived = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            session.Receiver.FileReceived += (_, _) =>
            {
                lock (receivedGate)
                {
                    receivedCount++;

                    if (receivedCount == paths.Count)
                    {
                        allReceived.TrySetResult();
                    }
                }
            };

            session.Receiver.FileFailed += (_, args) =>
                allReceived.TrySetException(args.Error);

            queue.EnqueueFiles(paths);

            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(60));
            await queue.StartAsync(cts.Token);

            var summary = await completed.Task.WaitAsync(
                TimeSpan.FromSeconds(10));
            await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(paths.Count, summary.CompletedCount);
            Assert.Equal(0, summary.FailedCount);
            Assert.Equal(0, summary.SkippedCount);
            Assert.Equal(paths.Count, receivedCount);
            Assert.Empty(Directory.GetFiles(receiveDirectory, "*.part"));
            Assert.Empty(Directory.GetFiles(receiveDirectory, "*.meta"));
        });
    }

    [Fact]
    public Task SendReceive_UnsafeAndUnicodeNames_StayInDestination()
    {
        return InTempDirectoryAsync(async root =>
        {
            var receiveDirectory = Path.Combine(root, "receive");
            var data = CreatePayload(4096);

            var traversal = await SendSingleAsync(
                receiveDirectory,
                @"..\..\escape.txt",
                data);

            Assert.Equal("escape.txt", traversal.FileName);
            Assert.Equal(
                Path.Combine(receiveDirectory, "escape.txt"),
                traversal.FilePath);
            Assert.False(File.Exists(Path.Combine(root, "escape.txt")));

            var reserved = await SendSingleAsync(
                receiveDirectory,
                "CON.txt",
                data);
            Assert.Equal("_CON.txt", reserved.FileName);

            var trailing = await SendSingleAsync(
                receiveDirectory,
                "name. . ",
                data);
            Assert.Equal("name", trailing.FileName);

            var invalid = await SendSingleAsync(
                receiveDirectory,
                "bad<>:\"/\\|?*.txt",
                data);
            Assert.DoesNotContain(
                invalid.FileName,
                character => Path.GetInvalidFileNameChars().Contains(character));
            Assert.Equal(data, File.ReadAllBytes(invalid.FilePath));

            var unicode = await SendSingleAsync(
                receiveDirectory,
                "中文-🚀.txt",
                data);
            Assert.Equal("中文-🚀.txt", unicode.FileName);
            Assert.Equal(data, File.ReadAllBytes(unicode.FilePath));

            var longName = new string('a', 150) + ".bin";
            var longResult = await SendSingleAsync(
                receiveDirectory,
                longName,
                data);
            Assert.Equal(longName, longResult.FileName);
            Assert.True(File.Exists(longResult.FilePath));
            Assert.Equal(data, File.ReadAllBytes(longResult.FilePath));
        });
    }

    [Fact]
    public Task Receive_DestinationPathIsFile_FailsFastWithRemoteError()
    {
        return InTempDirectoryAsync(async root =>
        {
            var receivePath = Path.Combine(root, "receive");
            await File.WriteAllTextAsync(receivePath, "blocker");

            await using var session = new TransferTestSession(receivePath);

            var exception = await Assert.ThrowsAsync<RemoteTransferException>(
                () => session.SendBytesAsync(
                    CreatePayload(64 * 1024),
                    "x.bin"));

            Assert.Equal(
                WifiDirectErrorCode.TransferInterrupted,
                exception.ErrorCode);
            Assert.Contains("接收目录", exception.Message);

            var failure = await session.Failed.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            Assert.IsAssignableFrom<IOException>(failure);
        });
    }

    [Fact]
    public Task Receive_FinalMoveBlockedByDirectory_SendsRemoteError()
    {
        return InTempDirectoryAsync(async root =>
        {
            var receiveDirectory = Path.Combine(root, "receive");
            Directory.CreateDirectory(receiveDirectory);
            Directory.CreateDirectory(
                Path.Combine(receiveDirectory, "conflict.bin"));

            await using var session = new TransferTestSession(
                receiveDirectory,
                sameNamePolicy: SameNamePolicy.Overwrite);

            var exception = await Assert.ThrowsAsync<RemoteTransferException>(
                () => session.SendBytesAsync(
                    CreatePayload(64 * 1024),
                    "conflict.bin"));

            Assert.Contains(
                exception.ErrorCode,
                new[]
                {
                    WifiDirectErrorCode.TransferInterrupted,
                    WifiDirectErrorCode.PermissionDenied
                });

            var failure = await session.Failed.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            Assert.True(
                failure is IOException or UnauthorizedAccessException);

            Assert.True(
                Directory.Exists(
                    Path.Combine(receiveDirectory, "conflict.bin")));
            Assert.Empty(Directory.GetFiles(receiveDirectory, "*.part"));
            Assert.Empty(Directory.GetFiles(receiveDirectory, "*.meta"));
        });
    }

    [Fact]
    public async Task Send_OversizedStream_RejectsBeforeWriting()
    {
        using var source = new FakeLengthStream(
            FileTransferProtocol.DefaultMaxFileSize + 1);
        var dispatcher = new MessageDispatcher(Stream.Null, Stream.Null);

        var exception = await Assert.ThrowsAsync<FileTooLargeException>(
            () => FileSender.SendAsync(
                dispatcher,
                source,
                "too-big.bin"));

        Assert.Equal(
            FileTransferProtocol.DefaultMaxFileSize + 1,
            exception.ActualSize);
        Assert.Equal(
            FileTransferProtocol.DefaultMaxFileSize,
            exception.MaxSize);
    }

    private static async Task<FileReceiveResult> SendSingleAsync(
        string receiveDirectory,
        string fileName,
        byte[] data)
    {
        await using var session = new TransferTestSession(receiveDirectory);

        await session.SendBytesAsync(data, fileName);

        return await session.Received.Task.WaitAsync(
            TimeSpan.FromSeconds(10));
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
            "wdt-boundary-" + Guid.NewGuid().ToString("N"));
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

    private sealed class FakeLengthStream : Stream
    {
        private readonly long _length;
        private long _position;

        public FakeLengthStream(long length)
        {
            _length = length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("超限文件不应被读取。");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("超限文件不应被读取。");

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}

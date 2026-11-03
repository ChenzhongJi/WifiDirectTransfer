using System.Security.Cryptography;
using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class FileTransferTests
{
    [Fact]
    public Task SendReceive_OneMiB_RoundTrips()
    {
        return InTempDirectoryAsync(async directory =>
        {
            var data = CreatePayload(1024 * 1024);
            var expectedHash = SHA256.HashData(data);

            await using var session = new TransferTestSession(directory);

            var sendResult = await session.SendBytesAsync(data, "payload.bin");
            var receiveResult = await session.Received.Task.WaitAsync(
                TimeSpan.FromSeconds(10));
            var receiverStarted = await session.Started.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            Assert.Equal(0, sendResult.StartOffset);
            Assert.Single(session.SenderStarts);
            Assert.Equal(0, session.SenderStarts[0].StartOffset);
            Assert.Equal(0, receiverStarted.StartInfo.StartOffset);
            Assert.Equal(data.Length, sendResult.FileSize);
            Assert.Equal(expectedHash, sendResult.Sha256);
            Assert.Equal(expectedHash, receiveResult.Sha256);
            Assert.Equal("payload.bin", receiveResult.FileName);
            Assert.Equal(data, File.ReadAllBytes(receiveResult.FilePath));
            Assert.Empty(Directory.GetFiles(directory, "*.part"));
            Assert.Empty(Directory.GetFiles(directory, "*.meta"));
        });
    }

    [Fact]
    public Task SendReceive_ZeroByteFile_CreatesEmptyFile()
    {
        return InTempDirectoryAsync(async directory =>
        {
            var data = Array.Empty<byte>();
            var expectedHash = SHA256.HashData(data);

            await using var session = new TransferTestSession(directory);

            var sendResult = await session.SendBytesAsync(data, "empty.bin");
            var receiveResult = await session.Received.Task.WaitAsync(
                TimeSpan.FromSeconds(10));

            var fileNameLength = FileHeaderCodec.EncodeFileName("empty.bin").Length;
            Assert.Equal(
                5 + FileTransferProtocol.FixedHeaderLength + fileNameLength +
                5 + MessageProtocol.FileEndDigestLength,
                session.Transport.ForwardLog.Length);

            Assert.Equal(0, sendResult.FileSize);
            Assert.Equal(expectedHash, sendResult.Sha256);
            Assert.Equal(expectedHash, receiveResult.Sha256);
            Assert.Equal(0, new FileInfo(receiveResult.FilePath).Length);
            Assert.Empty(File.ReadAllBytes(receiveResult.FilePath));
            Assert.Empty(Directory.GetFiles(directory, "*.part"));
            Assert.Empty(Directory.GetFiles(directory, "*.meta"));
        });
    }

    [Fact]
    public Task Receive_TamperedDigest_ThrowsAndDeletesPartAndMeta()
    {
        return InTempDirectoryAsync(async directory =>
        {
            var data = CreatePayload(1024 * 1024);
            using var wire = new MemoryStream();
            var sender = new MessageDispatcher(Stream.Null, wire);

            await SendManualFileAsync(
                sender,
                data,
                "payload.bin",
                tamperDigest: true);

            await Assert.ThrowsAsync<ChecksumMismatchException>(
                () => ReceiveWireAsync(wire, directory));

            Assert.Empty(Directory.GetFiles(directory));
        });
    }

    [Fact]
    public Task Receive_UnsafeFileName_StoresOnlySanitizedLeafName()
    {
        return InTempDirectoryAsync(async directory =>
        {
            var data = CreatePayload(4096);

            await using var session = new TransferTestSession(directory);

            await session.SendBytesAsync(data, @"..\CON");
            var receiveResult = await session.Received.Task.WaitAsync(
                TimeSpan.FromSeconds(10));

            Assert.Equal("_CON", receiveResult.FileName);
            Assert.Equal(Path.Combine(directory, "_CON"), receiveResult.FilePath);
            Assert.Equal(data, File.ReadAllBytes(receiveResult.FilePath));
            Assert.Empty(Directory.GetFiles(directory, "*.part"));
            Assert.Empty(Directory.GetFiles(directory, "*.meta"));
        });
    }

    [Fact]
    public Task InterleavedTextAndControlMessages_DoNotBreakFileReassembly()
    {
        return InTempDirectoryAsync(async directory =>
        {
            var data = CreatePayload(1024 * 1024);
            using var wire = new MemoryStream();
            var sender = new MessageDispatcher(Stream.Null, wire);

            var nameBytes = FileHeaderCodec.EncodeFileName("interleaved.bin");
            var codec = new FileHeaderCodec();
            var header = FileHeader.CreateFileStart(data.Length, nameBytes.Length);
            var startPayload = codec.SerializeFileStartPayload(header, nameBytes);

            await sender.SendAsync(MessageType.FileStart, startPayload, CancellationToken.None);

            var chunkIndex = 0;

            for (var offset = 0; offset < data.Length; offset += FileTransferProtocol.BlockSize)
            {
                var count = Math.Min(FileTransferProtocol.BlockSize, data.Length - offset);

                await sender.SendAsync(
                    MessageType.FileChunk,
                    data.AsMemory(offset, count),
                    CancellationToken.None);

                if (chunkIndex == 3)
                {
                    await sender.SendAsync(
                        MessageType.TextMessage,
                        TextMessageCodec.Encode("interleaved-text"),
                        CancellationToken.None);
                }

                if (chunkIndex == 7)
                {
                    await sender.SendAsync(
                        MessageType.ControlMessage,
                        ControlMessageCodec.SerializeAck(0),
                        CancellationToken.None);
                }

                chunkIndex++;
            }

            await sender.SendAsync(
                MessageType.FileEnd,
                SHA256.HashData(data),
                CancellationToken.None);

            var seenTypes = new List<MessageType>();
            var receiveResult = await ReceiveWireAsync(
                wire,
                directory,
                seenTypes.Add);

            Assert.Equal(data, File.ReadAllBytes(receiveResult.FilePath));
            Assert.Equal(SHA256.HashData(data), receiveResult.Sha256);
            Assert.Contains(MessageType.TextMessage, seenTypes);
            Assert.Contains(MessageType.ControlMessage, seenTypes);
            Assert.Empty(Directory.GetFiles(directory, "*.part"));
            Assert.Empty(Directory.GetFiles(directory, "*.meta"));
        });
    }

    [Fact]
    public Task Send_SourceShorterThanReportedLength_ThrowsFileChanged()
    {
        return InTempDirectoryAsync(async directory =>
        {
            await using var session = new TransferTestSession(directory);
            using var source = new ShortReadStream(
                new byte[512 * 1024],
                reportedLength: 1024 * 1024);

            var exception = await Assert.ThrowsAsync<FileChangedException>(
                () => session.SendAsync(source, "changed.bin"));

            Assert.Equal(1024 * 1024, exception.ExpectedLength);
            Assert.Equal(512 * 1024, exception.ActualLength);

            session.CompleteTransport();
            session.CancelSender();

            var failure = await session.Failed.Task.WaitAsync(
                TimeSpan.FromSeconds(10));
            Assert.IsType<TransferTruncatedException>(failure);
        });
    }

    [Fact]
    public async Task Send_OversizedFile_ThrowsBeforeWriting()
    {
        using var source = new MemoryStream(new byte[11]);
        var dispatcher = new MessageDispatcher(Stream.Null, Stream.Null);

        var exception = await Assert.ThrowsAsync<FileTooLargeException>(
            () => FileSender.SendAsync(dispatcher, source, "big.bin", maxFileSize: 10));

        Assert.Equal(11, exception.ActualSize);
        Assert.Equal(10, exception.MaxSize);
    }

    [Fact]
    public Task Resume_After512KiB_ContinuesFromPersistedOffset()
    {
        return InTempDirectoryAsync(async directory =>
        {
            var data = CreatePayload(1024 * 1024);
            const string fileName = "resume.bin";
            var partPath = Path.Combine(directory, fileName + ".part");
            var metaPath = Path.Combine(directory, fileName + ".meta");

            await using (var first = new TransferTestSession(directory))
            {
                using var shortSource = new ShortReadStream(
                    data.AsSpan(0, 512 * 1024).ToArray(),
                    reportedLength: data.Length);

                var changed = await Assert.ThrowsAsync<FileChangedException>(
                    () => first.SendAsync(shortSource, fileName));

                Assert.Equal(512 * 1024, changed.ActualLength);

                first.CompleteTransport();
                first.CancelSender();

                var failure = await first.Failed.Task.WaitAsync(
                    TimeSpan.FromSeconds(10));
                Assert.IsType<TransferTruncatedException>(failure);

                var senderStart = Assert.Single(first.SenderStarts);
                var receiverStart = await first.Started.Task.WaitAsync(
                    TimeSpan.FromSeconds(2));

                Assert.Equal(0, senderStart.StartOffset);
                Assert.Equal(0, receiverStart.StartInfo.StartOffset);
                Assert.Equal(512 * 1024, new FileInfo(partPath).Length);
                Assert.True(File.Exists(metaPath));
            }

            // 第一次连接已释放，.part/.meta 必须保留供续传。
            Assert.True(File.Exists(partPath));
            Assert.True(File.Exists(metaPath));

            await using (var second = new TransferTestSession(directory))
            {
                var sendResult = await second.SendBytesAsync(data, fileName);
                var completed = await Task.WhenAny(
                        second.Received.Task,
                        second.Failed.Task)
                    .WaitAsync(TimeSpan.FromSeconds(10));

                if (completed == second.Failed.Task)
                {
                    throw await second.Failed.Task;
                }

                var receiveResult = await second.Received.Task;
                var receiverStart = await second.Started.Task.WaitAsync(
                    TimeSpan.FromSeconds(2));

                var senderStart = Assert.Single(second.SenderStarts);

                Assert.Equal(512 * 1024, senderStart.StartOffset);
                Assert.Equal(512 * 1024, receiverStart.StartInfo.StartOffset);
                Assert.Equal(512 * 1024, sendResult.StartOffset);
                Assert.Equal(SHA256.HashData(data), sendResult.Sha256);
                Assert.Equal(SHA256.HashData(data), receiveResult.Sha256);
                Assert.Equal(data, File.ReadAllBytes(receiveResult.FilePath));
                Assert.False(File.Exists(partPath));
                Assert.False(File.Exists(metaPath));
            }
        });
    }

    private static async Task SendManualFileAsync(
        MessageDispatcher sender,
        byte[] data,
        string fileName,
        bool tamperDigest)
    {
        var nameBytes = FileHeaderCodec.EncodeFileName(fileName);
        var codec = new FileHeaderCodec();
        var header = FileHeader.CreateFileStart(data.Length, nameBytes.Length);
        var startPayload = codec.SerializeFileStartPayload(header, nameBytes);

        await sender.SendAsync(MessageType.FileStart, startPayload, CancellationToken.None);

        for (var offset = 0; offset < data.Length; offset += FileTransferProtocol.BlockSize)
        {
            var count = Math.Min(FileTransferProtocol.BlockSize, data.Length - offset);

            await sender.SendAsync(
                MessageType.FileChunk,
                data.AsMemory(offset, count),
                CancellationToken.None);
        }

        var digest = SHA256.HashData(data);

        if (tamperDigest)
        {
            digest[0] ^= 0xFF;
        }

        await sender.SendAsync(MessageType.FileEnd, digest, CancellationToken.None);
    }

    private static async Task<FileReceiveResult> ReceiveWireAsync(
        MemoryStream wire,
        string directory,
        Action<MessageType>? onMessage = null)
    {
        wire.Position = 0;

        var dispatcher = new MessageDispatcher(wire, Stream.Null);
        await using var receiver = new FileReceiver(dispatcher, directory);

        var received = new TaskCompletionSource<FileReceiveResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        receiver.FileReceived += (_, args) => received.TrySetResult(args.Result);
        receiver.FileFailed += (_, args) => failed.TrySetResult(args.Error);

        if (onMessage is not null)
        {
            dispatcher.MessageReceived += (_, args, _) =>
            {
                onMessage(args.Type);
                return Task.CompletedTask;
            };
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await dispatcher.StartReceiveLoopAsync(cts.Token);

        if (failed.Task.IsCompleted)
        {
            throw await failed.Task;
        }

        return await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
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

    private sealed class ShortReadStream : MemoryStream
    {
        private readonly long _reportedLength;

        public ShortReadStream(byte[] buffer, long reportedLength)
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

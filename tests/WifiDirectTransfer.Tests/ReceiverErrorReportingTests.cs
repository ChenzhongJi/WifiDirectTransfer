using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class ReceiverErrorReportingTests
{
    [Fact]
    public Task Receiver_OversizedChunk_SendsTransferErrorAndDeletesPartial()
    {
        return InTempDirectoryAsync(async directory =>
        {
            var result = await RunInvalidChunkScenarioAsync(
                directory,
                declaredSize: 128 * 1024,
                fileName: "oversized.bin",
                chunks: new[] { new byte[FileTransferProtocol.BlockSize + 1] });

            Assert.Equal(
                ControlMessageSubtype.TransferError,
                result.Error.Subtype);
            Assert.Equal(
                WifiDirectErrorCode.InvalidProtocolData,
                result.Error.ErrorCode);
            Assert.IsType<MessageProtocolException>(result.Failure);
            AssertNoPartialFiles(directory);
        });
    }

    [Fact]
    public Task Receiver_ChunkBeyondDeclaredSize_SendsTransferErrorAndDeletesPartial()
    {
        return InTempDirectoryAsync(async directory =>
        {
            var result = await RunInvalidChunkScenarioAsync(
                directory,
                declaredSize: 100,
                fileName: "overflow.bin",
                chunks: new[] { new byte[64], new byte[64] });

            Assert.Equal(
                ControlMessageSubtype.TransferError,
                result.Error.Subtype);
            Assert.Equal(
                WifiDirectErrorCode.InvalidProtocolData,
                result.Error.ErrorCode);
            Assert.IsType<MessageProtocolException>(result.Failure);
            AssertNoPartialFiles(directory);
        });
    }

    private static async Task<(ControlMessage Error, Exception Failure)>
        RunInvalidChunkScenarioAsync(
            string directory,
            long declaredSize,
            string fileName,
            IReadOnlyList<byte[]> chunks)
    {
        var transport = new TestDuplexTransport();
        var senderDispatcher = new MessageDispatcher(
            transport.SenderInput,
            transport.SenderOutput);
        var receiverDispatcher = new MessageDispatcher(
            transport.ReceiverInput,
            transport.ReceiverOutput);
        var receiver = new FileReceiver(receiverDispatcher, directory);

        var failed = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.FileFailed += (_, args) => failed.TrySetResult(args.Error);

        var transferError = new TaskCompletionSource<ControlMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        senderDispatcher.MessageReceived += (_, args, _) =>
        {
            if (args.Type != MessageType.ControlMessage)
            {
                return Task.CompletedTask;
            }

            try
            {
                var message = ControlMessageCodec.Deserialize(args.Payload.Span);

                if (message.Subtype == ControlMessageSubtype.TransferError)
                {
                    transferError.TrySetResult(message);
                }
            }
            catch (Exception)
            {
                // 测试只关心 TransferError。
            }

            return Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var senderLoop = senderDispatcher.StartReceiveLoopAsync(cts.Token);
        var receiverLoop = receiverDispatcher.StartReceiveLoopAsync(cts.Token);

        try
        {
            var fileNameBytes = FileHeaderCodec.EncodeFileName(fileName);
            var header = FileHeader.CreateFileStart(
                declaredSize,
                fileNameBytes.Length);
            var startPayload = new FileHeaderCodec()
                .SerializeFileStartPayload(header, fileNameBytes);

            await senderDispatcher.SendAsync(
                MessageType.FileStart,
                startPayload,
                cts.Token);

            foreach (var chunk in chunks)
            {
                await senderDispatcher.SendAsync(
                    MessageType.FileChunk,
                    chunk,
                    cts.Token);
            }

            var error = await transferError.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            var failure = await failed.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            return (error, failure);
        }
        finally
        {
            cts.Cancel();
            transport.CompleteBoth();

            try
            {
                await receiverLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 测试清理忽略循环退出异常。
            }

            try
            {
                await senderLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 测试清理忽略循环退出异常。
            }

            await receiver.DisposeAsync().ConfigureAwait(false);
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void AssertNoPartialFiles(string directory)
    {
        Assert.Empty(
            Directory.EnumerateFiles(
                directory,
                "*.part",
                SearchOption.AllDirectories));
        Assert.Empty(
            Directory.EnumerateFiles(
                directory,
                "*.meta",
                SearchOption.AllDirectories));
    }

    private static async Task InTempDirectoryAsync(Func<string, Task> test)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "wdt-receiver-error-" + Guid.NewGuid().ToString("N"));
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
                // 测试清理忽略文件占用。
            }
        }
    }
}

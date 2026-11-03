using System.Threading.Channels;

namespace WifiDirectTransfer.Tests;

internal sealed class TestDuplexTransport : IAsyncDisposable
{
    private readonly Channel<byte[]> _forward;
    private readonly Channel<byte[]> _backward;

    public TestDuplexTransport()
    {
        _forward = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        _backward = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

        ForwardLog = new MemoryStream();
        BackwardLog = new MemoryStream();

        SenderInput = new ChannelReaderStream(_backward.Reader);
        SenderOutput = new ChannelWriterStream(_forward.Writer, ForwardLog);
        ReceiverInput = new ChannelReaderStream(_forward.Reader);
        ReceiverOutput = new ChannelWriterStream(_backward.Writer, BackwardLog);
    }

    public Stream SenderInput { get; }

    public Stream SenderOutput { get; }

    public Stream ReceiverInput { get; }

    public Stream ReceiverOutput { get; }

    public MemoryStream ForwardLog { get; }

    public MemoryStream BackwardLog { get; }

    public void CompleteBoth()
    {
        _forward.Writer.TryComplete();
        _backward.Writer.TryComplete();
    }

    public ValueTask DisposeAsync()
    {
        CompleteBoth();
        ForwardLog.Dispose();
        BackwardLog.Dispose();
        return default;
    }

    private sealed class ChannelReaderStream : Stream
    {
        private readonly ChannelReader<byte[]> _reader;
        private byte[]? _current;
        private int _offset;

        public ChannelReaderStream(ChannelReader<byte[]> reader)
        {
            _reader = reader;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            while (_current is null || _offset >= _current.Length)
            {
                if (!await _reader
                        .WaitToReadAsync(cancellationToken)
                        .ConfigureAwait(false))
                {
                    return 0;
                }

                if (!_reader.TryRead(out var next))
                {
                    continue;
                }

                _current = next;
                _offset = 0;

                if (_current.Length == 0)
                {
                    _current = null;
                }
            }

            var count = Math.Min(buffer.Length, _current!.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class ChannelWriterStream : Stream
    {
        private readonly ChannelWriter<byte[]> _writer;
        private readonly Stream? _record;

        public ChannelWriterStream(ChannelWriter<byte[]> writer, Stream? record)
        {
            _writer = writer;
            _record = record;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var copy = buffer.ToArray();

            if (_record is not null)
            {
                await _record
                    .WriteAsync(copy, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (copy.Length > 0)
            {
                await _writer
                    .WriteAsync(copy, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}

internal sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;

    public InlineProgress(Action<T> report)
    {
        _report = report;
    }

    public void Report(T value) => _report(value);
}

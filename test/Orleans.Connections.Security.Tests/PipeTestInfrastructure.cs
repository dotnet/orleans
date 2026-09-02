using System.Buffers;
using System.IO.Pipelines;

namespace Orleans.Connections.Security.Tests;

internal sealed class TestDuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
{
    public PipeReader Input { get; } = input;

    public PipeWriter Output { get; } = output;
}

internal sealed class ScriptedPipeReader(Func<CancellationToken, ValueTask<ReadResult>> readAsync) : PipeReader
{
    private readonly List<CancellationToken> _readCancellationTokens = [];

    public SequencePosition LastConsumed { get; private set; }

    public SequencePosition LastExamined { get; private set; }

    public int AdvanceCount { get; private set; }

    public int CompleteCount { get; private set; }

    public int CompleteAsyncCount { get; private set; }

    public IReadOnlyList<CancellationToken> ReadCancellationTokens => _readCancellationTokens;

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        LastConsumed = consumed;
        LastExamined = examined;
        AdvanceCount++;
    }

    public override void CancelPendingRead()
    {
    }

    public override void Complete(Exception? exception = null) => CompleteCount++;

    public override ValueTask CompleteAsync(Exception? exception = null)
    {
        CompleteAsyncCount++;
        return default;
    }

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        _readCancellationTokens.Add(cancellationToken);
        return readAsync(cancellationToken);
    }

    public override bool TryRead(out ReadResult result)
    {
        result = default;
        return false;
    }
}

internal sealed class ControlledPipeReader : PipeReader
{
    private readonly TaskCompletionSource<ReadResult> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _readStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ReadStarted => _readStarted.Task;

    public SequencePosition LastConsumed { get; private set; }

    public int AdvanceCount { get; private set; }

    public CancellationToken LastReadCancellationToken { get; private set; }

    public void SetResult(ReadResult result) => _result.SetResult(result);

    public override void AdvanceTo(SequencePosition consumed)
    {
        LastConsumed = consumed;
        AdvanceCount++;
    }

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => AdvanceTo(consumed);

    public override void CancelPendingRead() => _result.TrySetCanceled();

    public override void Complete(Exception? exception = null)
    {
    }

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        LastReadCancellationToken = cancellationToken;
        _readStarted.TrySetResult();
        return new(_result.Task);
    }

    public override bool TryRead(out ReadResult result)
    {
        result = default;
        return false;
    }
}

internal sealed class RecordingPipeWriter(FlushResult flushResult) : PipeWriter
{
    private byte[] _buffer = new byte[256];
    private int _written;

    public ReadOnlyMemory<byte> Written => _buffer.AsMemory(0, _written);

    public int FlushCount { get; private set; }

    public int CompleteCount { get; private set; }

    public int CompleteAsyncCount { get; private set; }

    public CancellationToken LastFlushCancellationToken { get; private set; }

    public override void Advance(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (_written + bytes > _buffer.Length)
        {
            throw new InvalidOperationException("Advanced beyond the supplied buffer.");
        }

        _written += bytes;
    }

    public override void CancelPendingFlush()
    {
    }

    public override void Complete(Exception? exception = null) => CompleteCount++;

    public override ValueTask CompleteAsync(Exception? exception = null)
    {
        CompleteAsyncCount++;
        return default;
    }

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        LastFlushCancellationToken = cancellationToken;
        FlushCount++;
        return new(flushResult);
    }

    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public override Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void EnsureCapacity(int sizeHint)
    {
        var required = _written + Math.Max(sizeHint, 1);
        if (required > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(required, _buffer.Length * 2));
        }
    }
}

internal sealed class TrackingStream(Stream inner, bool disposeInner = false) : Stream
{
    public int DisposeCount { get; private set; }

    public int DisposeAsyncCount { get; private set; }

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => inner.Read(buffer);

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        inner.WriteAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeCount++;
            if (disposeInner)
            {
                inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        DisposeAsyncCount++;
        if (disposeInner)
        {
            await inner.DisposeAsync();
        }
    }
}

internal static class SequenceFactory
{
    public static ReadOnlySequence<byte> Create(params byte[][] segments)
    {
        ArgumentOutOfRangeException.ThrowIfZero(segments.Length);

        var first = new ByteSequenceSegment(segments[0]);
        var last = first;
        for (var i = 1; i < segments.Length; i++)
        {
            last = last.Append(segments[i]);
        }

        return new(first, 0, last, last.Memory.Length);
    }

    private sealed class ByteSequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public ByteSequenceSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public ByteSequenceSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new ByteSequenceSegment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length
            };
            Next = next;
            return next;
        }
    }
}

#nullable enable

using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Orleans.Serialization.Buffers;

namespace Orleans.Connections.Transport.Streams;

/// <summary>
/// <see cref="Stream"/> implementation which reads and writes to a <see cref="MessageTransport"/>.
/// </summary>
public class MessageTransportStream(MessageTransport transport, MemoryPool<byte> memoryPool) : Stream
{
    private readonly MessageTransport _transport = transport;
    private readonly StreamWriteRequest _writeRequest = new();
    private readonly StreamReadRequest _readRequest = new();

    /// <inheritdoc/>
    public override bool CanTimeout => true;

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => true;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <inheritdoc/>
    public MemoryPool<byte> MemoryPool { get; } = memoryPool;

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => Read(new Span<byte>(buffer, offset, count));

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => Write(new ReadOnlySpan<byte>(buffer, offset, count));

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer) => base.Read(buffer);

    /// <inheritdoc/>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _readRequest.Reset();
        _readRequest.SetBuffer(buffer);
        if (!_transport.EnqueueRead(_readRequest))
        {
            _readRequest.Reset();
            return new ValueTask<int>(0);
        }

        return _readRequest.OnProgressAsync();
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        // TODO: rent once and reuse, only returning on dispose / to rent a larger buffer / to restore a standard-sized buffer (in the case of huge writes)
        using var bytes = MemoryPool.Rent(buffer.Length);
        buffer.CopyTo(bytes.Memory.Span);
        WriteAsync(bytes.Memory, CancellationToken.None).AsTask().Wait();
    }

    /// <inheritdoc/>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _writeRequest.Reset();
        _writeRequest.Write(buffer);
        if (!_transport.EnqueueWrite(_writeRequest))
        {
            return ValueTask.FromException(new ObjectDisposedException("Network transport is unable to satisfy the request"));
        }

        // Wait for the request to complete;
        return _writeRequest.OnCompleteAsync();
    }

    /// <inheritdoc/>
    public override ValueTask DisposeAsync() => default;

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public override void Flush() { }

    private sealed class StreamWriteRequest : WriteRequest, IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<bool> _signal = new()
        {
            RunContinuationsAsynchronously = true
        };

        private readonly ArcBufferWriter _bufferWriter = new();
        public StreamWriteRequest()
        {
            Buffers = new(_bufferWriter);
        }

        public void Write(ReadOnlyMemory<byte> buffer) => _bufferWriter.Write(buffer.Span);
        public ValueTask OnCompleteAsync() => new(this, _signal.Version);
        public override void SetResult() => _signal.SetResult(true);
        public override void SetException(Exception error) => _signal.SetException(error);
        public void GetResult(short token) => _signal.GetResult(token);
        public ValueTaskSourceStatus GetStatus(short token) => _signal.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _signal.OnCompleted(continuation, state, token, flags);
        public void Reset() => _signal.Reset();
    }

    private sealed class StreamReadRequest : ReadRequest, IValueTaskSource<int>
    {
        private ManualResetValueTaskSourceCore<int> _completion = new();
        private Memory<byte> _buffer;

        public void SetBuffer(Memory<byte> buffer) => _buffer = buffer;

        public override bool OnRead(ArcBufferReader bufferReader)
        {
            var bytesRead = Math.Min(bufferReader.Length, _buffer.Length);
            if (bytesRead > 0)
            {
                bufferReader.Consume(_buffer.Span[..bytesRead]);
            }

            _completion.SetResult(bytesRead);
            return true;
        }

        public override void OnCanceled() => _completion.SetResult(0);

        public ValueTask<int> OnProgressAsync() => new(this, _completion.Version);
        public override void OnError(Exception error) => _completion.SetException(error);
        void IValueTaskSource<int>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _completion.OnCompleted(continuation, state, token, flags);
        int IValueTaskSource<int>.GetResult(short token) => _completion.GetResult(token);
        ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _completion.GetStatus(token);
        public void Reset() => _completion.Reset();
    }
}

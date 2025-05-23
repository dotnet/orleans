using System.Diagnostics;
using System.Numerics;

namespace Orleans.Journaling;

public sealed partial class LogExtentBuilder
{
    public sealed class ReadOnlyStream : Stream
    {
        private LogExtentBuilder? _builder;
        private int _length;
        private int _position;

        public ReadOnlyStream() { }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => SetPosition((int)value); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => throw new NotImplementedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    SetPosition((int)offset);
                    break;
                case SeekOrigin.Current:
                    SetPosition(_position + (int)offset);
                    break;
                case SeekOrigin.End:
                    SetPosition(_length - (int)offset);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(origin));
            }

            return Position;
        }

        private void SetPosition(int value)
        {
            if (value > _length || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _position = value;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Read(buffer, offset, count));

        public override void CopyTo(Stream destination, int bufferSize)
        {
            ValidateCopyToArguments(destination, bufferSize);
            _builder!.CopyToAsync(destination, bufferSize, default).AsTask().GetAwaiter().GetResult();
        }

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            ValidateCopyToArguments(destination, bufferSize);

            if (_position != 0) throw new NotImplementedException("Position must be zero for this copy operation");
            
            return _builder!.CopyToAsync(destination, bufferSize, cancellationToken).AsTask();
        }

        public override void Flush() => throw GetReadOnlyException();
        public override void WriteByte(byte value) => throw GetReadOnlyException();
        public override void SetLength(long value) => throw GetReadOnlyException();
        public override void Write(byte[] buffer, int offset, int count) => throw GetReadOnlyException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw GetReadOnlyException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw GetReadOnlyException();

        public void SetBuilder(LogExtentBuilder builder)
        {
            _builder = builder;
            _position = 0;
            _length = ComputeLength();
        }

        public void Reset()
        {
            _builder = default;
            _position = 0;
            _length = 0;
        }

        private int ComputeLength()
        {
            Debug.Assert(_builder!._entryLengths is not null);
            var length = _builder!._buffer.Length;
            foreach (var entry in _builder!._entryLengths)
            {
                length += GetVarIntWidth(entry);
            }

            return length;
        }

        private static int GetVarIntWidth(uint value) => 1 + (int)((uint)BitOperations.Log2(value) / 7);

        private static NotSupportedException GetReadOnlyException() => new("This stream is read-only");
    }
}

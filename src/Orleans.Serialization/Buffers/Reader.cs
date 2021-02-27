using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
#if NETCOREAPP
using System.Numerics;
#endif
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.Serialization.Session;
#if !NETCOREAPP
using Orleans.Serialization.Utilities;
#endif

namespace Orleans.Serialization.Buffers
{
    public abstract class ReaderInput
    {
        public abstract long Position { get; }
        public abstract long Length { get; }
        public abstract void Skip(long count);
        public abstract void Seek(long position);
        public abstract byte ReadByte();
        public abstract uint ReadUInt32();
        public abstract ulong ReadUInt64();
        public abstract void ReadBytes(in Span<byte> destination);
        public abstract void ReadBytes(byte[] destination, int offset, int length);
        public abstract bool TryReadBytes(int length, out ReadOnlySpan<byte> bytes);
    }

    internal sealed class StreamReaderInput : ReaderInput
    {
        [ThreadStatic]
        private static byte[] Scratch;

        private readonly Stream _stream;
        private readonly ArrayPool<byte> _memoryPool;

        public override long Position => _stream.Position;
        public override long Length => _stream.Length;

        public StreamReaderInput(Stream stream, ArrayPool<byte> memoryPool)
        {
            _stream = stream;
            _memoryPool = memoryPool;
        }

        public override byte ReadByte()
        {
            var c = _stream.ReadByte();
            if (c < 0)
            {
                ThrowInsufficientData();
            }

            return (byte)c;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void ReadBytes(in Span<byte> destination)
        {
#if NETCOREAPP
            var count = _stream.Read(destination);
            if (count < destination.Length)
            {
                ThrowInsufficientData();
            }
#else
            byte[] array = default;
            try
            {
                array = _memoryPool.Rent(destination.Length);
                var count = _stream.Read(array, 0, destination.Length);
                if (count < destination.Length)
                {
                    ThrowInsufficientData();
                }

                array.CopyTo(destination);
            }
            finally
            {
                if (array is object)
                {
                    _memoryPool.Return(array);
                }
            }
#endif
        }

        public override void ReadBytes(byte[] destination, int offset, int length)
        {
            var count = _stream.Read(destination, offset, length);
            if (count < length)
            {
                ThrowInsufficientData();
            }
        }

#if NET5_0
        [SkipLocalsInit]
#endif
        public override uint ReadUInt32()
        {
#if NETCOREAPP
            Span<byte> buffer = stackalloc byte[sizeof(uint)];
            ReadBytes(buffer);
            return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
#else
            var buffer = GetScratchBuffer();
            ReadBytes(buffer, 0, sizeof(uint));
            return BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, sizeof(uint)));
#endif
        }

#if NET5_0
        [SkipLocalsInit]
#endif
        public override ulong ReadUInt64()
        {
#if NETCOREAPP
            Span<byte> buffer = stackalloc byte[sizeof(ulong)];
            ReadBytes(buffer);
            return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
#else
            var buffer = GetScratchBuffer();
            ReadBytes(buffer, 0, sizeof(ulong));
            return BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, sizeof(ulong)));
#endif
        }

        public override void Skip(long count) => _ = _stream.Seek(count, SeekOrigin.Current);

        public override void Seek(long position) => _ = _stream.Seek(position, SeekOrigin.Begin);

        public override bool TryReadBytes(int length, out ReadOnlySpan<byte> destination)
        {
            // Cannot get a span pointing to a stream's internal buffer.
            destination = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInsufficientData() => throw new InvalidOperationException("Insufficient data present in buffer.");

        private static byte[] GetScratchBuffer() => Scratch ??= new byte[1024];
    }

    public static class Reader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader<ReaderInput> Create(Stream stream, SerializerSession session) => new Reader<ReaderInput>(new StreamReaderInput(stream, ArrayPool<byte>.Shared), session, 0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader<ReadOnlySequence<byte>> Create(ReadOnlySequence<byte> sequence, SerializerSession session) => new Reader<ReadOnlySequence<byte>>(sequence, session, 0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader<SpanReaderInput> Create(ReadOnlySpan<byte> buffer, SerializerSession session) => new Reader<SpanReaderInput>(buffer, session, 0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader<SpanReaderInput> Create(byte[] buffer, SerializerSession session) => new Reader<SpanReaderInput>(buffer, session, 0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader<SpanReaderInput> Create(ReadOnlyMemory<byte> buffer, SerializerSession session) => new Reader<SpanReaderInput>(buffer.Span, session, 0);
    }

    public readonly struct SpanReaderInput
    {
    }

    public ref struct Reader<TInput>
    {
        private readonly static bool IsSpanInput = typeof(TInput) == typeof(SpanReaderInput);
        private readonly static bool IsReadOnlySequenceInput = typeof(TInput) == typeof(ReadOnlySequence<byte>);
        private readonly static bool IsReaderInput = typeof(ReaderInput).IsAssignableFrom(typeof(TInput));
        
        private ReadOnlySpan<byte> _currentSpan;
        private SequencePosition _nextSequencePosition;
        private int _bufferPos;
        private int _bufferSize;
        private long _previousBuffersSize;
        private readonly long _sequenceOffset;
        private TInput _input;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Reader(TInput input, SerializerSession session, long globalOffset)
        {
            if (IsReadOnlySequenceInput)
            {
                ref var sequence = ref Unsafe.As<TInput, ReadOnlySequence<byte>>(ref input);
                _input = input;
                _nextSequencePosition = sequence.Start;
                _currentSpan = sequence.First.Span;
                _bufferPos = 0;
                _bufferSize = _currentSpan.Length;
                _previousBuffersSize = 0;
                _sequenceOffset = globalOffset;
            }
            else if (IsReaderInput)
            {
                _input = input;
                _nextSequencePosition = default;
                _currentSpan = default;
                _bufferPos = 0;
                _bufferSize = default;
                _previousBuffersSize = 0;
                _sequenceOffset = globalOffset;
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(TInput)} is not supported by this constructor");
            }

            Session = session;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Reader(ReadOnlySpan<byte> input, SerializerSession session, long globalOffset)
        {
            if (IsSpanInput)
            {
                _input = default;
                _nextSequencePosition = default;
                _currentSpan = input; 
                _bufferPos = 0;
                _bufferSize = _currentSpan.Length;
                _previousBuffersSize = 0;
                _sequenceOffset = globalOffset;
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(TInput)} is not supported by this constructor");
            }

            Session = session;
        }

        public SerializerSession Session { get; }

        public long Position
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (IsReadOnlySequenceInput)
                {
                    return _sequenceOffset + _previousBuffersSize + _bufferPos;
                }
                else if (IsSpanInput)
                {
                    return _sequenceOffset + _bufferPos;
                }
                else if (_input is ReaderInput readerInput)
                {
                    return readerInput.Position;
                }
                else
                {
                    return ThrowNotSupportedInput<long>();
                }
            }
        }

        public long Length
        {
            get
            {
                if (IsReadOnlySequenceInput)
                {
                    return Unsafe.As<TInput, ReadOnlySequence<byte>>(ref _input).Length;
                }
                else if (IsSpanInput)
                {
                    return _currentSpan.Length;
                }
                else if (_input is ReaderInput readerInput)
                {
                    return readerInput.Length;
                }
                else
                {
                    return ThrowNotSupportedInput<long>();
                }
            }
        }

        public void Skip(long count)
        {
            if (IsReadOnlySequenceInput)
            {
                var end = Position + count;
                while (Position < end)
                {
                    if (Position + _bufferSize >= end)
                    {
                        _bufferPos = (int)(end - _previousBuffersSize);
                    }
                    else
                    {
                        MoveNext();
                    }
                }
            }
            else if (IsSpanInput)
            {
                _bufferPos += (int)count;
                if (_bufferPos > _currentSpan.Length || count > int.MaxValue)
                {
                    ThrowInsufficientData();
                }
            }
            else if (_input is ReaderInput input)
            {
                input.Skip(count);
            }
            else
            {
                ThrowNotSupportedInput();
            }
        }

        /// <summary>
        /// Creates a new reader beginning at the specified position.
        /// </summary>
        public void ForkFrom(long position, out Reader<TInput> forked)
        {
            if (IsReadOnlySequenceInput)
            {
                ref var sequence = ref Unsafe.As<TInput, ReadOnlySequence<byte>>(ref _input);
                var slicedSequence = sequence.Slice(position - _sequenceOffset);
                forked = new Reader<TInput>(Unsafe.As<ReadOnlySequence<byte>, TInput>(ref slicedSequence), Session, position);

                if (forked.Position != position)
                {
                    ThrowInvalidPosition(position, forked.Position);
                }
            }
            else if (IsSpanInput)
            {
                forked = new Reader<TInput>(_currentSpan.Slice((int)position), Session, position);
                if (forked.Position != position || position > int.MaxValue)
                {
                    ThrowInvalidPosition(position, forked.Position);
                }
            }
            else if (_input is ReaderInput input)
            {
                input.Seek(position);
                forked = new Reader<TInput>(_input, Session, 0);

                if (forked.Position != position)
                {
                    ThrowInvalidPosition(position, forked.Position);
                }
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(TInput)} is not supported");
            }
            
            [MethodImpl(MethodImplOptions.NoInlining)]
            static void ThrowInvalidPosition(long expectedPosition, long actualPosition)
            {
                throw new InvalidOperationException($"Expected to arrive at position {expectedPosition} after {nameof(ForkFrom)}, but resulting position is {actualPosition}");
            }
        }

        public void ResumeFrom(long position)
        {
            if (IsReadOnlySequenceInput)
            {
                // Nothing is required.
            }
            else if (IsSpanInput)
            {
                // Nothing is required.
            }
            else if (_input is ReaderInput input)
            {
                // Seek the input stream.
                input.Seek(Position);
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(TInput)} is not supported");
            }

            if (position != Position)
            {
                ThrowInvalidPosition(position, Position);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            static void ThrowInvalidPosition(long expectedPosition, long actualPosition)
            {
                throw new InvalidOperationException($"Expected to arrive at position {expectedPosition} after {nameof(ResumeFrom)}, but resulting position is {actualPosition}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void MoveNext()
        {
            if (IsReadOnlySequenceInput)
            {
                ref var sequence = ref Unsafe.As<TInput, ReadOnlySequence<byte>>(ref _input);
                _previousBuffersSize += _bufferSize;

                // If this is the first call to MoveNext then nextSequencePosition is invalid and must be moved to the second position.
                if (_nextSequencePosition.Equals(sequence.Start))
                {
                    _ = sequence.TryGet(ref _nextSequencePosition, out _);
                }

                if (!sequence.TryGet(ref _nextSequencePosition, out var memory))
                {
                    _currentSpan = memory.Span;
                    ThrowInsufficientData();
                }

                _currentSpan = memory.Span;
                _bufferPos = 0;
                _bufferSize = _currentSpan.Length;
            }
            else if (IsSpanInput)
            {
                ThrowInsufficientData();
            }
            else
            {
                ThrowNotSupportedInput();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadByte()
        {
            if (IsReadOnlySequenceInput || IsSpanInput)
            {
                var pos = _bufferPos;
                var span = _currentSpan;
                if ((uint)pos >= (uint)span.Length)
                {
                    return ReadByteSlow(ref this);
                }

                var result = span[pos];
                _bufferPos = pos + 1;
                return result;
            }
            else if (_input is ReaderInput readerInput)
            {
                return readerInput.ReadByte();
            }
            else
            {
                return ThrowNotSupportedInput<byte>();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static byte ReadByteSlow(ref Reader<TInput> reader)
        {
            reader.MoveNext();
            return reader._currentSpan[reader._bufferPos++];

        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadInt32() => (int)ReadUInt32();

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadUInt32()
        {
            if (IsReadOnlySequenceInput || IsSpanInput)
            {
                const int width = 4;
                if (_bufferPos + width > _bufferSize)
                {
                    return ReadSlower(ref this);
                }

                var result = BinaryPrimitives.ReadUInt32LittleEndian(_currentSpan.Slice(_bufferPos, width));
                _bufferPos += width;
                return result;

                static uint ReadSlower(ref Reader<TInput> r)
                {
                    uint b1 = r.ReadByte();
                    uint b2 = r.ReadByte();
                    uint b3 = r.ReadByte();
                    uint b4 = r.ReadByte();

                    return b1 | (b2 << 8) | (b3 << 16) | (b4 << 24);
                }
            }
            else if (_input is ReaderInput readerInput)
            {
                return readerInput.ReadUInt32();
            }
            else
            {
                return ThrowNotSupportedInput<uint>();
            }
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long ReadInt64() => (long)ReadUInt64();

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ReadUInt64()
        {
            if (IsReadOnlySequenceInput || IsSpanInput)
            {
                const int width = 8;
                if (_bufferPos + width > _bufferSize)
                {
                    return ReadSlower(ref this);
                }

                var result = BinaryPrimitives.ReadUInt64LittleEndian(_currentSpan.Slice(_bufferPos, width));
                _bufferPos += width;
                return result;

                static ulong ReadSlower(ref Reader<TInput> r)
                {
                    ulong b1 = r.ReadByte();
                    ulong b2 = r.ReadByte();
                    ulong b3 = r.ReadByte();
                    ulong b4 = r.ReadByte();
                    ulong b5 = r.ReadByte();
                    ulong b6 = r.ReadByte();
                    ulong b7 = r.ReadByte();
                    ulong b8 = r.ReadByte();

                    return b1 | (b2 << 8) | (b3 << 16) | (b4 << 24)
                           | (b5 << 32) | (b6 << 40) | (b7 << 48) | (b8 << 56);
                }
            }
            else if (_input is ReaderInput readerInput)
            {
                return readerInput.ReadUInt64();
            }
            else
            {
                return ThrowNotSupportedInput<uint>();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInsufficientData() => throw new InvalidOperationException("Insufficient data present in buffer.");

        public byte[] ReadBytes(uint count)
        {
            if (count == 0)
            {
                return Array.Empty<byte>();
            }

            if (count > 10240 && count > Length)
            {
                ThrowInvalidSizeException(count);
            }

            var bytes = new byte[count];
            if (IsReadOnlySequenceInput || IsSpanInput)
            {
                var destination = new Span<byte>(bytes);
                ReadBytes(in destination);
            }
            else if (_input is ReaderInput readerInput)
            {
                readerInput.ReadBytes(bytes, 0, (int)count);
            }

            return bytes;
        }

        public void ReadBytes(in Span<byte> destination)
        {
            if (IsReadOnlySequenceInput || IsSpanInput)
            {
                if (_bufferPos + destination.Length <= _bufferSize)
                {
                    _currentSpan.Slice(_bufferPos, destination.Length).CopyTo(destination);
                    _bufferPos += destination.Length;
                    return;
                }

                CopySlower(in destination, ref this);

                static void CopySlower(in Span<byte> d, ref Reader<TInput> reader)
                {
                    var dest = d;
                    while (true)
                    {
                        var writeSize = Math.Min(dest.Length, reader._currentSpan.Length - reader._bufferPos);
                        reader._currentSpan.Slice(reader._bufferPos, writeSize).CopyTo(dest);
                        reader._bufferPos += writeSize;
                        dest = dest.Slice(writeSize);

                        if (dest.Length == 0)
                        {
                            break;
                        }

                        reader.MoveNext();
                    }
                }
            }
            else if (_input is ReaderInput readerInput)
            {
                readerInput.ReadBytes(in destination);
            }
            else
            {
                ThrowNotSupportedInput();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryReadBytes(int length, out ReadOnlySpan<byte> bytes)
        {
            if (IsReadOnlySequenceInput || IsSpanInput)
            {
                if (_bufferPos + length <= _bufferSize)
                {
                    bytes = _currentSpan.Slice(_bufferPos, length);
                    _bufferPos += length;
                    return true;
                }

                bytes = default;
                return false;
            }
            else if (_input is ReaderInput readerInput)
            {
                return readerInput.TryReadBytes(length, out bytes);
            }
            else
            {
                bytes = default;
                return ThrowNotSupportedInput<bool>();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal uint ReadVarUInt32NoInlining() => ReadVarUInt32();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe uint ReadVarUInt32()
        {
            if (IsReadOnlySequenceInput || IsSpanInput)
            {
                var pos = _bufferPos;

                if (!BitConverter.IsLittleEndian || pos + 8 > _currentSpan.Length)
                {
                    return ReadVarUInt32Slow();
                }

                // The number of zeros in the msb position dictates the number of bytes to be read.
                // Up to a maximum of 5 for a 32bit integer.
                ref byte readHead = ref Unsafe.Add(ref MemoryMarshal.GetReference(_currentSpan), pos);

                ulong result = Unsafe.ReadUnaligned<ulong>(ref readHead);
                var bytesNeeded = BitOperations.TrailingZeroCount(result) + 1;
                result >>= bytesNeeded;
                _bufferPos += bytesNeeded;

                // Mask off invalid data
                var fullWidthReadMask = ~((ulong)bytesNeeded - 6 + 1);
                var mask = ((1UL << (bytesNeeded * 7)) - 1) | fullWidthReadMask;
                result &= mask;

                return (uint)result;
            }
            else
            {
                return ReadVarUInt32Slow();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private uint ReadVarUInt32Slow()
        {
            var header = ReadByte();
            var numBytes = BitOperations.TrailingZeroCount(0x0100U | header) + 1;

            // Widen to a ulong for the 5-byte case
            ulong result = header;

            // Read additional bytes as needed
            var shiftBy = 8;
            var i = numBytes;
            while (--i > 0)
            {
                result |= (ulong)ReadByte() << shiftBy;
                shiftBy += 8;
            }

            result >>= numBytes;
            return (uint)result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ReadVarUInt64()
        {
            if (IsReadOnlySequenceInput || IsSpanInput)
            {
                var pos = _bufferPos;

                if (!BitConverter.IsLittleEndian || pos + 10 > _currentSpan.Length)
                {
                    return ReadVarUInt64Slow();
                }

                // The number of zeros in the msb position dictates the number of bytes to be read.
                // Up to a maximum of 5 for a 32bit integer.
                ref byte readHead = ref Unsafe.Add(ref MemoryMarshal.GetReference(_currentSpan), pos);

                ulong result = Unsafe.ReadUnaligned<ulong>(ref readHead);

                var bytesNeeded = BitOperations.TrailingZeroCount(result) + 1;
                result >>= bytesNeeded;
                _bufferPos += bytesNeeded;

                ushort upper = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref readHead, sizeof(ulong)));
                result |= ((ulong)upper) << (64 - bytesNeeded);

                // Mask off invalid data
                var fullWidthReadMask = ~((ulong)bytesNeeded - 10 + 1);
                var mask = ((1UL << (bytesNeeded * 7)) - 1) | fullWidthReadMask;
                result &= mask;

                return result;
            }
            else
            {
                return ReadVarUInt64Slow();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ulong ReadVarUInt64Slow()
        {
            var header = ReadByte();
            var numBytes = BitOperations.TrailingZeroCount(0x0100U | header) + 1;

            // Widen to a ulong for the 5-byte case
            ulong result = header;

            // Read additional bytes as needed
            if (numBytes < 9)
            {
                var shiftBy = 8;
                var i = numBytes;
                while (--i > 0)
                {
                    result |= (ulong)ReadByte() << shiftBy;
                    shiftBy += 8;
                }

                result >>= numBytes;
                return result;
            }
            else
            {
                result |= (ulong)ReadByte() << 8;

                // If there was more than one byte worth of trailing zeros, read again now that we have more data.
                numBytes = BitOperations.TrailingZeroCount(result) + 1;

                if (numBytes == 9)
                {
                    result |= (ulong)ReadByte() << 16;
                    result |= (ulong)ReadByte() << 24;
                    result |= (ulong)ReadByte() << 32;

                    result |= (ulong)ReadByte() << 40;
                    result |= (ulong)ReadByte() << 48;
                    result |= (ulong)ReadByte() << 56;
                    result >>= 9;

                    var upper = (ushort)ReadByte();
                    result |= ((ulong)upper) << (64 - 9);
                    return result;
                }
                else if (numBytes == 10)
                {
                    result |= (ulong)ReadByte() << 16;
                    result |= (ulong)ReadByte() << 24;
                    result |= (ulong)ReadByte() << 32;

                    result |= (ulong)ReadByte() << 40;
                    result |= (ulong)ReadByte() << 48;
                    result |= (ulong)ReadByte() << 56;
                    result >>= 10;

                    var upper = (ushort)(ReadByte() | (ushort)(ReadByte() << 8));
                    result |= ((ulong)upper) << (64 - 10);
                    return result;
                }
            }

            return ExceptionHelper.ThrowArgumentOutOfRange<ulong>("value");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T ThrowNotSupportedInput<T>() => throw new NotSupportedException($"Type {typeof(TInput)} is not supported");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowNotSupportedInput() => throw new NotSupportedException($"Type {typeof(TInput)} is not supported");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidSizeException(uint length) => throw new IndexOutOfRangeException(
            $"Declared length of {typeof(byte[])}, {length}, is greater than total length of input.");
    }

}
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
#if NETCOREAPP3_1_OR_GREATER
using System.Numerics;
#endif
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.Serialization.Session;
#if !NETCOREAPP3_1_OR_GREATER
using Orleans.Serialization.Utilities;
#endif

namespace Orleans.Serialization.Buffers
{
    /// <summary>
    /// Functionality for reading binary data.
    /// </summary>
    public abstract class ReaderInput
    {
        /// <summary>
        /// Gets the position.
        /// </summary>
        /// <value>The position.</value>
        public abstract long Position { get; }

        /// <summary>
        /// Gets the length.
        /// </summary>
        /// <value>The length.</value>
        public abstract long Length { get; }

        /// <summary>
        /// Skips the specified number of bytes.
        /// </summary>
        /// <param name="count">The number of bytes to skip.</param>
        public abstract void Skip(long count);

        /// <summary>
        /// Seeks to the specified position.
        /// </summary>
        /// <param name="position">The position.</param>
        public abstract void Seek(long position);

        /// <summary>
        /// Reads a byte from the input.
        /// </summary>
        /// <returns>The byte which was read.</returns>
        public abstract byte ReadByte();

        /// <summary>
        /// Reads a <see cref="uint"/> from the input.
        /// </summary>
        /// <returns>The <see cref="uint"/> which was read.</returns>
        public abstract uint ReadUInt32();

        /// <summary>
        /// Reads a <see cref="ulong"/> from the input.
        /// </summary>
        /// <returns>The <see cref="ulong"/> which was read.</returns>
        public abstract ulong ReadUInt64();

        /// <summary>
        /// Fills the destination span with data from the input.
        /// </summary>
        /// <param name="destination">The destination.</param>
        public abstract void ReadBytes(in Span<byte> destination);

        /// <summary>
        /// Reads bytes from the input into the destination array.
        /// </summary>
        /// <param name="destination">The destination array.</param>
        /// <param name="offset">The offset into the destination to start writing bytes.</param>
        /// <param name="length">The number of bytes to copy into destination.</param>
        public abstract void ReadBytes(byte[] destination, int offset, int length);

        /// <summary>
        /// Tries to read the specified number of bytes from the input.
        /// </summary>
        /// <param name="length">The number of bytes to read..</param>
        /// <param name="bytes">The bytes which were read..</param>
        /// <returns><see langword="true"/> if the number of bytes were successfully read, <see langword="false"/> otherwise.</returns>
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
#if NETCOREAPP3_1_OR_GREATER
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

#if NET5_0_OR_GREATER
        [SkipLocalsInit]
#endif
        public override uint ReadUInt32()
        {
#if NETCOREAPP3_1_OR_GREATER
            Span<byte> buffer = stackalloc byte[sizeof(uint)];
            ReadBytes(buffer);
            return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
#else
            var buffer = GetScratchBuffer();
            ReadBytes(buffer, 0, sizeof(uint));
            return BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, sizeof(uint)));
#endif
        }

#if NET5_0_OR_GREATER
        [SkipLocalsInit]
#endif
        public override ulong ReadUInt64()
        {
#if NETCOREAPP3_1_OR_GREATER
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

    /// <summary>
    /// Helper methods for <see cref="Reader{TInput}"/>.
    /// </summary>
    public static class Reader
    {
        /// <summary>
        /// Creates a reader for the provided input stream.
        /// </summary>
        /// <param name="stream">The stream.</param>
        /// <param name="session">The session.</param>
        /// <returns>A new <see cref="Reader{TInput}"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader<ReaderInput> Create(Stream stream, SerializerSession session) => new Reader<ReaderInput>(new StreamReaderInput(stream, ArrayPool<byte>.Shared), session, 0);

        /// <summary>
        /// Creates a reader for the provided input data.
        /// </summary>
        /// <param name="sequence">The input data.</param>
        /// <param name="session">The session.</param>
        /// <returns>A new <see cref="Reader{TInput}"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader<ReadOnlySequence<byte>> Create(ReadOnlySequence<byte> sequence, SerializerSession session) => new Reader<ReadOnlySequence<byte>>(sequence, session, 0);

        /// <summary>
        /// Creates a reader for the provided input data.
        /// </summary>
        /// <param name="buffer">The input data.</param>
        /// <param name="session">The session.</param>
        /// <returns>A new <see cref="Reader{TInput}"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader<SpanReaderInput> Create(ReadOnlySpan<byte> buffer, SerializerSession session) => new Reader<SpanReaderInput>(buffer, session, 0);

        /// <summary>
        /// Creates a reader for the provided input data.
        /// </summary>
        /// <param name="buffer">The input data.</param>
        /// <param name="session">The session.</param>
        /// <returns>A new <see cref="Reader{TInput}"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader<SpanReaderInput> Create(byte[] buffer, SerializerSession session) => new Reader<SpanReaderInput>(buffer, session, 0);

        /// <summary>
        /// Creates a reader for the provided input data.
        /// </summary>
        /// <param name="buffer">The input data.</param>
        /// <param name="session">The session.</param>
        /// <returns>A new <see cref="Reader{TInput}"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader<SpanReaderInput> Create(ReadOnlyMemory<byte> buffer, SerializerSession session) => new Reader<SpanReaderInput>(buffer.Span, session, 0);
    }

    /// <summary>
    /// Marker type for <see cref="Reader{TInput}"/> objects which operate over <see cref="ReadOnlySpan{Byte}"/> buffers.
    /// </summary>
    public readonly struct SpanReaderInput
    {
    }

    /// <summary>
    /// Provides functionality for parsing data from binary input.
    /// </summary>
    /// <typeparam name="TInput">The underlying buffer reader type.</typeparam>
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

        /// <summary>
        /// Gets the serializer session.
        /// </summary>
        /// <value>The serializer session.</value>
        public SerializerSession Session { get; }

        /// <summary>
        /// Gets the current reader position.
        /// </summary>
        /// <value>The current position.</value>
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

        /// <summary>
        /// Gets the input length.
        /// </summary>
        /// <value>The input length.</value>
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

        /// <summary>
        /// Skips the specified number of bytes.
        /// </summary>
        /// <param name="count">The number of bytes to skip.</param>
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
        /// <param name="position">
        /// The position in the input stream to fork from.
        /// </param>        
        /// <param name="forked">
        /// The forked reader instance.
        /// </param>        
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
                throw new InvalidOperationException($"Expected to arrive at position {expectedPosition} after ForkFrom, but resulting position is {actualPosition}");
            }
        }
        
        /// <summary>
        /// Resumes the reader from the specified position after forked readers are no longer in use.
        /// </summary>
        /// <param name="position">
        /// The position to resume reading from.
        /// </param>
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
                throw new InvalidOperationException($"Expected to arrive at position {expectedPosition} after ResumeFrom, but resulting position is {actualPosition}");
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

        /// <summary>
        /// Reads a byte from the input.
        /// </summary>
        /// <returns>The byte which was read.</returns>
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

        /// <summary>
        /// Reads an <see cref="int"/> from the input.
        /// </summary>
        /// <returns>The <see cref="int"/> which was read.</returns>
        public int ReadInt32() => (int)ReadUInt32();

        /// <summary>
        /// Reads a <see cref="uint"/> from the input.
        /// </summary>
        /// <returns>The <see cref="uint"/> which was read.</returns>
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

        /// <summary>
        /// Reads a <see cref="long"/> from the input.
        /// </summary>
        /// <returns>The <see cref="long"/> which was read.</returns>
        public long ReadInt64() => (long)ReadUInt64();

        /// <summary>
        /// Reads a <see cref="ulong"/> from the input.
        /// </summary>
        /// <returns>The <see cref="ulong"/> which was read.</returns>
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

        /// <summary>
        /// Reads the specified number of bytes into the provided writer.
        /// </summary>
        public void ReadBytes<TBufferWriter>(ref TBufferWriter writer, int count) where TBufferWriter : IBufferWriter<byte>
        {
            int chunkSize;
            for (var remaining = count; remaining > 0; remaining -= chunkSize)
            {
                var span = writer.GetSpan();
                if (span.Length > remaining)
                {
                    span = span[..remaining];
                }

                ReadBytes(in span);
                chunkSize = span.Length;
                writer.Advance(chunkSize);
            }
        }

        /// <summary>
        /// Reads an array of bytes from the input.
        /// </summary>
        /// <param name="count">The length of the array to read.</param>
        /// <returns>The array wihch was read.</returns>
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

        /// <summary>
        /// Fills <paramref name="destination"/> with bytes read from the input.
        /// </summary>
        /// <param name="destination">The destination.</param>
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

        /// <summary>
        /// Tries the read the specified number of bytes from the input.
        /// </summary>
        /// <param name="length">The length.</param>
        /// <param name="bytes">The bytes which were read.</param>
        /// <returns><see langword="true"/> if the specified number of bytes were read from the input, <see langword="false"/> otherwise.</returns>
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

        /// <summary>
        /// Reads a variable-width <see cref="uint"/> from the input.
        /// </summary>
        /// <returns>The <see cref="uint"/> which was read.</returns>
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

        /// <summary>
        /// Reads a variable-width <see cref="ulong"/> from the input.
        /// </summary>
        /// <returns>The <see cref="ulong"/> which was read.</returns>
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
//#define TRACE_SERIALIZATION
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    /// <summary>
    /// Reader for Orleans binary token streams
    /// </summary>
    internal sealed class BinaryTokenStreamReader2 : IBinaryTokenStreamReader
    {
        // ReSharper disable FieldCanBeMadeReadOnly.Local
        private ReadOnlySequence<byte> input;
        // ReSharper restore FieldCanBeMadeReadOnly.Local

        private ReadOnlyMemory<byte> currentSpan;
        private SequencePosition nextSequencePosition;
        private int bufferPos;
        private int bufferSize;
        private long previousBuffersSize;

        public BinaryTokenStreamReader2()
        {
        }

        public BinaryTokenStreamReader2(ReadOnlySequence<byte> input)
        {
            this.PartialReset(input);
        }
        public long Length => this.input.Length;
        
        public long Position
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => this.previousBuffersSize + this.bufferPos;
        }

        public int CurrentPosition
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (int)this.previousBuffersSize + this.bufferPos;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PartialReset(ReadOnlySequence<byte> input)
        {
            this.input = input;
            this.nextSequencePosition = input.Start;
            this.currentSpan = input.First;
            this.bufferPos = 0;
            this.bufferSize = this.currentSpan.Length;
            this.previousBuffersSize = 0;
        }

        public void Skip(long count)
        {
            var end = this.Position + count;
            while (this.Position < end)
            {
                if (this.Position + this.bufferSize >= end)
                {
                    this.bufferPos = (int)(end - this.previousBuffersSize);
                }
                else
                {
                    this.MoveNext();
                }
            }
        }

        /// <summary>
        /// Creates a new reader beginning at the specified position.
        /// </summary>
        public BinaryTokenStreamReader2 ForkFrom(long position)
        {
            var result = new BinaryTokenStreamReader2();
            var sliced = this.input.Slice(position);
            result.PartialReset(sliced);
            return result;
        }   

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MoveNext()
        {
            this.previousBuffersSize += this.bufferSize;

            // If this is the first call to MoveNext then nextSequencePosition is invalid and must be moved to the second position.
            if (this.nextSequencePosition.Equals(this.input.Start)) this.input.TryGet(ref this.nextSequencePosition, out _);

            if (!this.input.TryGet(ref this.nextSequencePosition, out var memory))
            {
                this.currentSpan = memory;
                ThrowInsufficientData();
            }

            this.currentSpan = memory;
            this.bufferPos = 0;
            this.bufferSize = this.currentSpan.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadByte()
        {
            if (this.bufferPos == this.bufferSize) this.MoveNext();
            return this.currentSpan.Span[this.bufferPos++];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte PeekByte()
        {
            if (this.bufferPos == this.bufferSize) this.MoveNext();
            return this.currentSpan.Span[this.bufferPos];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public short ReadInt16() => unchecked((short)ReadUInt16());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort ReadUInt16()
        {
            const int width = 2;
            if (this.bufferPos + width > this.bufferSize) return ReadSlower();

            var result = BinaryPrimitives.ReadUInt16LittleEndian(this.currentSpan.Span.Slice(this.bufferPos, width));
            this.bufferPos += width;
            return result;

            ushort ReadSlower()
            {
                ushort b1 = ReadByte();
                ushort b2 = ReadByte();

                return (ushort)(b1 | (b2 << 8));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadInt32() => (int)this.ReadUInt32();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadUInt32()
        {
            const int width = 4;
            if (this.bufferPos + width > this.bufferSize) return ReadSlower();

            var result = BinaryPrimitives.ReadUInt32LittleEndian(this.currentSpan.Span.Slice(this.bufferPos, width));
            this.bufferPos += width;
            return result;

            uint ReadSlower()
            {
                uint b1 = ReadByte();
                uint b2 = ReadByte();
                uint b3 = ReadByte();
                uint b4 = ReadByte();

                return b1 | (b2 << 8) | (b3 << 16) | (b4 << 24);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long ReadInt64() => (long)this.ReadUInt64();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ReadUInt64()
        {
            const int width = 8;
            if (this.bufferPos + width > this.bufferSize) return ReadSlower();

            var result = BinaryPrimitives.ReadUInt64LittleEndian(this.currentSpan.Slice(this.bufferPos, width).Span);
            this.bufferPos += width;
            return result;

            ulong ReadSlower()
            {
                ulong b1 = ReadByte();
                ulong b2 = ReadByte();
                ulong b3 = ReadByte();
                ulong b4 = ReadByte();
                ulong b5 = ReadByte();
                ulong b6 = ReadByte();
                ulong b7 = ReadByte();
                ulong b8 = ReadByte();

                return b1 | (b2 << 8) | (b3 << 16) | (b4 << 24)
                       | (b5 << 32) | (b6 << 40) | (b7 << 48) | (b8 << 56);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInsufficientData() => throw new InvalidOperationException("Insufficient data present in buffer.");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float ReadFloat() => BitConverter.Int32BitsToSingle(ReadInt32());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadInt64());

        public decimal ReadDecimal()
        {
            var parts = new[] { this.ReadInt32(), this.ReadInt32(), this.ReadInt32(), this.ReadInt32() };
            return new decimal(parts);
        }

        public byte[] ReadBytes(uint count)
        {
            if (count == 0)
            {
                return Array.Empty<byte>();
            }

            var bytes = new byte[count];
            var destination = new Span<byte>(bytes);
            this.ReadBytes(in destination);
            return bytes;
        }

        public void ReadBytes(in Span<byte> destination)
        {
            if (this.bufferPos + destination.Length <= this.bufferSize)
            {
                this.currentSpan.Slice(this.bufferPos, destination.Length).Span.CopyTo(destination);
                this.bufferPos += destination.Length;
                return;
            }

            CopySlower(in destination);

            void CopySlower(in Span<byte> d)
            {
                var dest = d;
                while (true)
                {
                    var writeSize = Math.Min(dest.Length, this.currentSpan.Length - this.bufferPos);
                    this.currentSpan.Slice(this.bufferPos, writeSize).Span.CopyTo(dest);
                    this.bufferPos += writeSize;
                    dest = dest.Slice(writeSize);

                    if (dest.Length == 0) break;

                    this.MoveNext();
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryReadBytes(int length, out ReadOnlySpan<byte> bytes)
        {
            if (this.bufferPos + length <= this.bufferSize)
            {
                bytes = this.currentSpan.Slice(this.bufferPos, length).Span;
                this.bufferPos += length;
                return true;
            }

            bytes = default;
            return false;
        }

        /// <summary> Read a <c>bool</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public bool ReadBoolean()
        {
            return ReadToken() == SerializationTokenType.True;
        }

        public DateTime ReadDateTime()
        {
            var n = this.ReadInt64();
            return n == 0 ? default(DateTime) : DateTime.FromBinary(n);
        }

        /// <summary> Read an <c>string</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public string ReadString()
        {
            var n = this.ReadInt32();
            if (n <= 0)
            {
                if (n == 0) return string.Empty;

                // a length of -1 indicates that the string is null.
                if (n == -1) return null;
            }

            if (this.bufferSize - this.bufferPos >= n)
            {
                var s = Encoding.UTF8.GetString(this.currentSpan.Slice(this.bufferPos, n).Span);
                this.bufferPos += n;
                return s;
            }
            else if (n <= 256)
            {
                Span<byte> bytes = stackalloc byte[n];
                this.ReadBytes(in bytes);
                return Encoding.UTF8.GetString(bytes);
            }
            else
            {
                var bytes = this.ReadBytes((uint)n);
                return Encoding.UTF8.GetString(bytes);
            }
        }

        /// <summary> Read the next bytes from the stream. </summary>
        /// <param name="destination">Output array to store the returned data in.</param>
        /// <param name="offset">Offset into the destination array to write to.</param>
        /// <param name="count">Number of bytes to read.</param>
        public void ReadByteArray(byte[] destination, int offset, int count)
        {
            if (offset + count > destination.Length)
            {
                throw new ArgumentOutOfRangeException("count", "Reading into an array that is too small");
            }

            if (count > 0)
            {
                var destSpan = new Span<byte>(destination, offset, count);
                this.ReadBytes(in destSpan);
            }
        }

        /// <summary> Read an <c>char</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public char ReadChar()
        {
            return Convert.ToChar(ReadInt16());
        }
        
        /// <summary> Read an <c>sbyte</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public sbyte ReadSByte()
        {
            return unchecked((sbyte)ReadByte());
        }

        /// <summary> Read an <c>IPAddress</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public IPAddress ReadIPAddress()
        {
            Span<byte> buff = stackalloc byte[16];
            ReadBytes(buff);
            bool v4 = true;
            for (var i = 0; i < 12; i++)
            {
                if (buff[i] != 0)
                {
                    v4 = false;
                    break;
                }
            }

            if (v4)
            {
                return new IPAddress(buff.Slice(12));
            }
            else
            {
                return new IPAddress(buff);
            }
        }

        public Guid ReadGuid()
        {
            Span<byte> bytes = stackalloc byte[16];
            this.ReadBytes(in bytes);
            return new Guid(bytes);
        }

        /// <summary> Read an <c>IPEndPoint</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public IPEndPoint ReadIPEndPoint()
        {
            var addr = ReadIPAddress();
            var port = ReadInt32();
            return new IPEndPoint(addr, port);
        }

        /// <summary> Read an <c>SiloAddress</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public SiloAddress ReadSiloAddress()
        {
            var ep = ReadIPEndPoint();
            var gen = ReadInt32();
            return SiloAddress.New(ep, gen);
        }

        public TimeSpan ReadTimeSpan()
        {
            return new TimeSpan(ReadInt64());
        }

        /// <summary>
        /// Read a block of data into the specified output <c>Array</c>.
        /// </summary>
        /// <param name="array">Array to output the data to.</param>
        /// <param name="n">Number of bytes to read.</param>
        public void ReadBlockInto(Array array, int n)
        {
            Buffer.BlockCopy(this.ReadBytes((uint)n), 0, array, 0, n);
        }

        /// <summary> Read a <c>SerializationTokenType</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        internal SerializationTokenType ReadToken()
        {
            return (SerializationTokenType)this.ReadByte();
        }
        
        public IBinaryTokenStreamReader Copy()
        {
            var result = new BinaryTokenStreamReader2();
            result.PartialReset(this.input);
            return result;
        }

        public int ReadInt() => this.ReadInt32();

        public uint ReadUInt() => this.ReadUInt32();

        public short ReadShort() => this.ReadInt16();

        public ushort ReadUShort() => this.ReadUInt16();

        public long ReadLong() => this.ReadInt64();

        public ulong ReadULong() => this.ReadUInt64();

        public byte[] ReadBytes(int count) => this.ReadBytes((uint)count);
    }
}

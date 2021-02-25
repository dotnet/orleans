using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Orleans.CodeGeneration;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    /// <summary>
    /// Writer for Orleans binary token streams
    /// </summary>
    internal sealed class BinaryTokenStreamWriter2<TBufferWriter> : IBinaryTokenStreamWriter where TBufferWriter : IBufferWriter<byte>
    {
        private static readonly Dictionary<Type, SerializationTokenType> typeTokens;
        private static readonly Dictionary<Type, Action<BinaryTokenStreamWriter2<TBufferWriter>, object>> writers;
        private static readonly Encoding Utf8Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

        private readonly Encoder utf8Encoder = Utf8Encoding.GetEncoder();
        private TBufferWriter output;
        private Memory<byte> currentBuffer;
        private int currentOffset;
        private int completedLength;

        static BinaryTokenStreamWriter2()
        {
            typeTokens = new Dictionary<Type, SerializationTokenType>();
            typeTokens[typeof(bool)] = SerializationTokenType.Boolean;
            typeTokens[typeof(int)] = SerializationTokenType.Int;
            typeTokens[typeof(uint)] = SerializationTokenType.Uint;
            typeTokens[typeof(short)] = SerializationTokenType.Short;
            typeTokens[typeof(ushort)] = SerializationTokenType.Ushort;
            typeTokens[typeof(long)] = SerializationTokenType.Long;
            typeTokens[typeof(ulong)] = SerializationTokenType.Ulong;
            typeTokens[typeof(byte)] = SerializationTokenType.Byte;
            typeTokens[typeof(sbyte)] = SerializationTokenType.Sbyte;
            typeTokens[typeof(float)] = SerializationTokenType.Float;
            typeTokens[typeof(double)] = SerializationTokenType.Double;
            typeTokens[typeof(decimal)] = SerializationTokenType.Decimal;
            typeTokens[typeof(string)] = SerializationTokenType.String;
            typeTokens[typeof(char)] = SerializationTokenType.Character;
            typeTokens[typeof(Guid)] = SerializationTokenType.Guid;
            typeTokens[typeof(DateTime)] = SerializationTokenType.Date;
            typeTokens[typeof(TimeSpan)] = SerializationTokenType.TimeSpan;
            typeTokens[typeof(GrainId)] = SerializationTokenType.GrainId;
            typeTokens[typeof(ActivationId)] = SerializationTokenType.ActivationId;
            typeTokens[typeof(SiloAddress)] = SerializationTokenType.SiloAddress;
            typeTokens[typeof(ActivationAddress)] = SerializationTokenType.ActivationAddress;
            typeTokens[typeof(IPAddress)] = SerializationTokenType.IpAddress;
            typeTokens[typeof(IPEndPoint)] = SerializationTokenType.IpEndPoint;
            typeTokens[typeof(CorrelationId)] = SerializationTokenType.CorrelationId;
            typeTokens[typeof(InvokeMethodRequest)] = SerializationTokenType.Request;
            typeTokens[typeof(Response)] = SerializationTokenType.Response;
            typeTokens[typeof(Dictionary<string, object>)] = SerializationTokenType.StringObjDict;
            typeTokens[typeof(Object)] = SerializationTokenType.Object;
            typeTokens[typeof(List<>)] = SerializationTokenType.List;
            typeTokens[typeof(SortedList<,>)] = SerializationTokenType.SortedList;
            typeTokens[typeof(Dictionary<,>)] = SerializationTokenType.Dictionary;
            typeTokens[typeof(HashSet<>)] = SerializationTokenType.Set;
            typeTokens[typeof(SortedSet<>)] = SerializationTokenType.SortedSet;
            typeTokens[typeof(KeyValuePair<,>)] = SerializationTokenType.KeyValuePair;
            typeTokens[typeof(LinkedList<>)] = SerializationTokenType.LinkedList;
            typeTokens[typeof(Stack<>)] = SerializationTokenType.Stack;
            typeTokens[typeof(Queue<>)] = SerializationTokenType.Queue;
            typeTokens[typeof(Tuple<>)] = SerializationTokenType.Tuple + 1;
            typeTokens[typeof(Tuple<,>)] = SerializationTokenType.Tuple + 2;
            typeTokens[typeof(Tuple<,,>)] = SerializationTokenType.Tuple + 3;
            typeTokens[typeof(Tuple<,,,>)] = SerializationTokenType.Tuple + 4;
            typeTokens[typeof(Tuple<,,,,>)] = SerializationTokenType.Tuple + 5;
            typeTokens[typeof(Tuple<,,,,,>)] = SerializationTokenType.Tuple + 6;
            typeTokens[typeof(Tuple<,,,,,,>)] = SerializationTokenType.Tuple + 7;

            writers = new Dictionary<Type, Action<BinaryTokenStreamWriter2<TBufferWriter>, object>>();
            writers[typeof(bool)] = (stream, obj) => stream.Write((bool)obj);
            writers[typeof(int)] = (stream, obj) => { stream.Write(SerializationTokenType.Int); stream.Write((int)obj); };
            writers[typeof(uint)] = (stream, obj) => { stream.Write(SerializationTokenType.Uint); stream.Write((uint)obj); };
            writers[typeof(short)] = (stream, obj) => { stream.Write(SerializationTokenType.Short); stream.Write((short)obj); };
            writers[typeof(ushort)] = (stream, obj) => { stream.Write(SerializationTokenType.Ushort); stream.Write((ushort)obj); };
            writers[typeof(long)] = (stream, obj) => { stream.Write(SerializationTokenType.Long); stream.Write((long)obj); };
            writers[typeof(ulong)] = (stream, obj) => { stream.Write(SerializationTokenType.Ulong); stream.Write((ulong)obj); };
            writers[typeof(byte)] = (stream, obj) => { stream.Write(SerializationTokenType.Byte); stream.Write((byte)obj); };
            writers[typeof(sbyte)] = (stream, obj) => { stream.Write(SerializationTokenType.Sbyte); stream.Write((sbyte)obj); };
            writers[typeof(float)] = (stream, obj) => { stream.Write(SerializationTokenType.Float); stream.Write((float)obj); };
            writers[typeof(double)] = (stream, obj) => { stream.Write(SerializationTokenType.Double); stream.Write((double)obj); };
            writers[typeof(decimal)] = (stream, obj) => { stream.Write(SerializationTokenType.Decimal); stream.Write((decimal)obj); };
            writers[typeof(string)] = (stream, obj) => { stream.Write(SerializationTokenType.String); stream.Write((string)obj); };
            writers[typeof(char)] = (stream, obj) => { stream.Write(SerializationTokenType.Character); stream.Write((char)obj); };
            writers[typeof(Guid)] = (stream, obj) => { stream.Write(SerializationTokenType.Guid); stream.Write((Guid)obj); };
            writers[typeof(DateTime)] = (stream, obj) => { stream.Write(SerializationTokenType.Date); stream.Write((DateTime)obj); };
            writers[typeof(TimeSpan)] = (stream, obj) => { stream.Write(SerializationTokenType.TimeSpan); stream.Write((TimeSpan)obj); };
            writers[typeof(GrainId)] = (stream, obj) => { stream.Write(SerializationTokenType.GrainId); stream.Write((GrainId)obj); };
            writers[typeof(ActivationId)] = (stream, obj) => { stream.Write(SerializationTokenType.ActivationId); stream.Write((ActivationId)obj); };
            writers[typeof(SiloAddress)] = (stream, obj) => { stream.Write(SerializationTokenType.SiloAddress); stream.Write((SiloAddress)obj); };
            writers[typeof(ActivationAddress)] = (stream, obj) => { stream.Write(SerializationTokenType.ActivationAddress); stream.Write((ActivationAddress)obj); };
            writers[typeof(IPAddress)] = (stream, obj) => { stream.Write(SerializationTokenType.IpAddress); stream.Write((IPAddress)obj); };
            writers[typeof(IPEndPoint)] = (stream, obj) => { stream.Write(SerializationTokenType.IpEndPoint); stream.Write((IPEndPoint)obj); };
            writers[typeof(CorrelationId)] = (stream, obj) => { stream.Write(SerializationTokenType.CorrelationId); stream.Write((CorrelationId)obj); };
        }
        
        public BinaryTokenStreamWriter2(TBufferWriter output)
        {
            this.PartialReset(output);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PartialReset(TBufferWriter output)
        {
            this.output = output;
            this.currentBuffer = output.GetMemory();
            this.currentOffset = default;
            this.completedLength = default;
        }

        /// <summary> Current write position in the stream. </summary>
        public int CurrentOffset { get { return this.Length; } }

        /// <summary>
        /// Commit the currently written buffers.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Commit()
        {
            this.output.Advance(this.currentOffset);
            this.completedLength += this.currentOffset;
            this.currentBuffer = default;
            this.currentOffset = default;
        }

        public void Write(decimal d)
        {
            this.Write(Decimal.GetBits(d));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(string s)
        {
            if (s is null)
            {
                this.Write(-1);
            }
            else
            {
                var enc = this.utf8Encoder;
                enc.Reset();

                // Attempt a fast write. Note that we could accurately determine the required bytes to encode the input here,
                // but that requires an additional scan over the string. We could determine an upper bound here, too, but that may
                // be wasteful. Instead, we will fall back to a slower and more accurate method if it is not.
                var writableSpan = this.TryGetContiguous(256);
                enc.Convert(s, writableSpan.Slice(4), true, out _, out var bytesUsed, out var completed);

                if (completed)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(writableSpan, bytesUsed);
                    this.currentOffset += 4 + bytesUsed;
                    return;
                }

                // Otherwise, try again more slowly, overwriting whatever data was just copied to the output buffer.
                this.WriteStringInternalSlower(s);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void WriteStringInternalSlower(string s)
        {
            var count = Encoding.UTF8.GetByteCount(s);
            this.Write(count);
            var span = this.TryGetContiguous(count);
            if (span.Length > count)
            {
                Encoding.UTF8.GetBytes(s, span);
                this.currentOffset += count;
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(s);
                this.WriteMultiSegment(bytes);
            }
        }

        public void Write(char c)
        {
            this.Write(Convert.ToInt16(c));
        }
        
        public void Write(bool b)
        {
            this.Write((byte)(b ? SerializationTokenType.True : SerializationTokenType.False));
        }
        
        public void WriteNull()
        {
            this.Write((byte)SerializationTokenType.Null);
        }

        public void WriteTypeHeader(Type t, Type expected = null)
        {
            if (t == expected)
            {
                this.Write((byte)SerializationTokenType.ExpectedType);
                return;
            }

            this.Write((byte)SerializationTokenType.SpecifiedType);

            if (t.IsArray)
            {
                this.Write((byte)(SerializationTokenType.Array + (byte)t.GetArrayRank()));
                this.WriteTypeHeader(t.GetElementType());
                return;
            }

            SerializationTokenType token;
            if (typeTokens.TryGetValue(t, out token))
            {
                this.Write((byte)token);
                return;
            }

            if (t.GetTypeInfo().IsGenericType)
            {
                if (typeTokens.TryGetValue(t.GetGenericTypeDefinition(), out token))
                {
                    this.Write((byte)token);
                    foreach (var tp in t.GetGenericArguments())
                    {
                        this.WriteTypeHeader(tp);
                    }
                    return;
                }
            }

            this.Write((byte)SerializationTokenType.NamedType);
            var typeKey = t.OrleansTypeKey();
            this.Write(typeKey.Length);
            this.Write(typeKey);
        }
                
        public void Write(byte[] b, int offset, int count)
        {
            if (count <= 0)
            {
                return;
            }

            if ((offset == 0) && (count == b.Length))
            {
                this.Write(b);
            }
            else
            {
                var temp = new byte[count];
                Buffer.BlockCopy(b, offset, temp, 0, count);
                this.Write(temp);
            }
        }
        
        public void Write(IPEndPoint ep)
        {
            this.Write(ep.Address);
            this.Write(ep.Port);
        }
        
        public void Write(IPAddress ip)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                for (var i = 0; i < 12; i++)
                {
                    this.Write((byte)0);
                }
                
                this.Write(ip.GetAddressBytes()); // IPv4 -- 4 bytes
            }
            else
            {
                this.Write(ip.GetAddressBytes()); // IPv6 -- 16 bytes
            }
        }
        
        public void Write(SiloAddress addr)
        {
            this.Write(addr.Endpoint);
            this.Write(addr.Generation);
        }
        
        public void Write(TimeSpan ts)
        {
            this.Write(ts.Ticks);
        }

        public void Write(DateTime dt)
        {
            this.Write(dt.ToBinary());
        }

        public void Write(Guid id)
        {
            this.Write(id.ToByteArray());
        }

        /// <summary>
        /// Try to write a simple type (non-array) value to the stream.
        /// </summary>
        /// <param name="obj">Input object to be written to the output stream.</param>
        /// <returns>Returns <c>true</c> if the value was successfully written to the output stream.</returns>
        public bool TryWriteSimpleObject(object obj)
        {
            if (obj == null)
            {
                this.WriteNull();
                return true;
            }
            Action<BinaryTokenStreamWriter2<TBufferWriter>, object> writer;
            if (writers.TryGetValue(obj.GetType(), out writer))
            {
                writer(this, obj);
                return true;
            }
            return false;
        }

        public int Length => this.currentOffset + this.completedLength;

        private Span<byte> WritableSpan => this.currentBuffer.Slice(this.currentOffset).Span;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureContiguous(int length)
        {
            // The current buffer is adequate.
            if (this.currentOffset + length < this.currentBuffer.Length) return;

            // The current buffer is inadequate, allocate another.
            this.Allocate(length);
#if DEBUG
            // Throw if the allocation does not satisfy the request.
            if (this.currentBuffer.Length < length) ThrowTooLarge(length);

            void ThrowTooLarge(int l) => throw new InvalidOperationException($"Requested buffer length {l} cannot be satisfied by the writer.");
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<byte> TryGetContiguous(int length)
        {
            // The current buffer is adequate.
            if (this.currentOffset + length > this.currentBuffer.Length)
            {
                // The current buffer is inadequate, allocate another.
                this.Allocate(length);
            }

            return this.WritableSpan;
        }

        public void Allocate(int length)
        {
            // Commit the bytes which have been written.
            this.output.Advance(this.currentOffset);

            // Request a new buffer with at least the requested number of available bytes.
            this.currentBuffer = this.output.GetMemory(length);

            // Update internal state for the new buffer.
            this.completedLength += this.currentOffset;
            this.currentOffset = 0;
        }

        public void Write(byte[] array)
        {
            // Fast path, try copying to the current buffer.
            if (array.Length <= this.currentBuffer.Length - this.currentOffset)
            {
                array.CopyTo(this.WritableSpan);
                this.currentOffset += array.Length;
            }
            else
            {
                var value = new ReadOnlySpan<byte>(array);
                this.WriteMultiSegment(in value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ReadOnlySpan<byte> value)
        {
            // Fast path, try copying to the current buffer.
            if (value.Length <= this.currentBuffer.Length - this.currentOffset)
            {
                value.CopyTo(this.WritableSpan);
                this.currentOffset += value.Length;
            }
            else
            {
                this.WriteMultiSegment(in value);
            }
        }

        private void WriteMultiSegment(in ReadOnlySpan<byte> source)
        {
            var input = source;
            while (true)
            {
                // Write as much as possible/necessary into the current segment.
                var writeSize = Math.Min(this.currentBuffer.Length - this.currentOffset, input.Length);
                input.Slice(0, writeSize).CopyTo(this.WritableSpan);
                this.currentOffset += writeSize;

                input = input.Slice(writeSize);

                if (input.Length == 0) return;

                // The current segment is full but there is more to write.
                this.Allocate(input.Length);
            }
        }

        public void Write(List<ArraySegment<byte>> b)
        {
            foreach (var segment in b)
            {
                this.Write(segment);
            }
        }

        public void Write(short[] array)
        {
            this.Write(MemoryMarshal.Cast<short, byte>(array));
        }

        public void Write(int[] array)
        {
            this.Write(MemoryMarshal.Cast<int, byte>(array));
        }

        public void Write(long[] array)
        {
            this.Write(MemoryMarshal.Cast<long, byte>(array));
        }

        public void Write(ushort[] array)
        {
            this.Write(MemoryMarshal.Cast<ushort, byte>(array));
        }

        public void Write(uint[] array)
        {
            this.Write(MemoryMarshal.Cast<uint, byte>(array));
        }

        public void Write(ulong[] array)
        {
            this.Write(MemoryMarshal.Cast<ulong, byte>(array));
        }

        public void Write(sbyte[] array)
        {
            this.Write(MemoryMarshal.Cast<sbyte, byte>(array));
        }

        public void Write(char[] array)
        {
            this.Write(MemoryMarshal.Cast<char, byte>(array));
        }

        public void Write(bool[] array)
        {
            this.Write(MemoryMarshal.Cast<bool, byte>(array));
        }

        public void Write(float[] array)
        {
            this.Write(MemoryMarshal.Cast<float, byte>(array));
        }

        public void Write(double[] array)
        {
            this.Write(MemoryMarshal.Cast<double, byte>(array));
        }

        public void Write(byte b)
        {
            const int width = sizeof(byte);
            this.EnsureContiguous(width);
            this.WritableSpan[0] = b;
            this.currentOffset += width;
        }

        public void Write(sbyte b)
        {
            const int width = sizeof(sbyte);
            this.EnsureContiguous(width);
            this.WritableSpan[0] = (byte)b;
            this.currentOffset += width;
        }

        public void Write(float i)
        {
            ReadOnlySpan<float> span = stackalloc float[1] { i };
            this.Write(MemoryMarshal.Cast<float, byte>(span));
        }

        public void Write(double i)
        {
            ReadOnlySpan<double> span = stackalloc double[1] { i };
            this.Write(MemoryMarshal.Cast<double, byte>(span));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(short value)
        {
            const int width = sizeof(short);
            this.EnsureContiguous(width);
            BinaryPrimitives.WriteInt16LittleEndian(this.WritableSpan, value);
            this.currentOffset += width;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(int value)
        {
            const int width = sizeof(int);
            this.EnsureContiguous(width);
            BinaryPrimitives.WriteInt32LittleEndian(this.WritableSpan, value);
            this.currentOffset += width;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(long value)
        {
            const int width = sizeof(long);
            this.EnsureContiguous(width);
            BinaryPrimitives.WriteInt64LittleEndian(this.WritableSpan, value);
            this.currentOffset += width;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(uint value)
        {
            const int width = sizeof(uint);
            this.EnsureContiguous(width);
            BinaryPrimitives.WriteUInt32LittleEndian(this.WritableSpan, value);
            this.currentOffset += width;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ushort value)
        {
            const int width = sizeof(ushort);
            this.EnsureContiguous(width);
            BinaryPrimitives.WriteUInt16LittleEndian(this.WritableSpan, value);
            this.currentOffset += width;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ulong value)
        {
            const int width = sizeof(ulong);
            this.EnsureContiguous(width);
            BinaryPrimitives.WriteUInt64LittleEndian(this.WritableSpan, value);
            this.currentOffset += width;
        }
    }
}

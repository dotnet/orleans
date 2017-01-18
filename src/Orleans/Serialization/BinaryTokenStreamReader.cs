//#define TRACE_SERIALIZATION
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using Orleans.CodeGeneration;
using Orleans.GrainDirectory;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    /// <summary>
    /// Reader for Orleans binary token streams
    /// </summary>
    public class BinaryTokenStreamReader
    {
        private readonly IList<ArraySegment<byte>> buffers;
        private readonly int buffersCount;
        private int currentSegmentIndex;
        private ArraySegment<byte> currentSegment;
        private byte[] currentBuffer;
        private int currentOffset;
        private int currentSegmentOffset;
        private int currentSegmentCount;
        private int totalProcessedBytes;
        private int currentSegmentOffsetPlusCount;
        private readonly int totalLength;

        private static readonly ArraySegment<byte> emptySegment = new ArraySegment<byte>(new byte[0]);
        private static readonly byte[] emptyByteArray = new byte[0];

        /// <summary>
        /// Create a new BinaryTokenStreamReader to read from the specified input byte array.
        /// </summary>
        /// <param name="input">Input binary data to be tokenized.</param>
        public BinaryTokenStreamReader(byte[] input)
            : this(new List<ArraySegment<byte>> { new ArraySegment<byte>(input) })
        {
        }

        /// <summary>
        /// Create a new BinaryTokenStreamReader to read from the specified input buffers.
        /// </summary>
        /// <param name="buffs">The list of ArraySegments to use for the data.</param>
        public BinaryTokenStreamReader(IList<ArraySegment<byte>> buffs)
        {
            buffers = buffs;
            totalProcessedBytes = 0;
            currentSegmentIndex = 0;
            InitializeCurrentSegment(0);
            totalLength = buffs.Sum(b => b.Count);
            buffersCount = buffs.Count;
            Trace("Starting new stream reader");
        }

        private void InitializeCurrentSegment(int segmentIndex)
        {
            currentSegment = buffers[segmentIndex];
            currentBuffer = currentSegment.Array;
            currentOffset = currentSegment.Offset;
            currentSegmentOffset = currentOffset;
            currentSegmentCount = currentSegment.Count;
            currentSegmentOffsetPlusCount = currentSegmentOffset + currentSegmentCount;
        }

        /// <summary>
        /// Create a new BinaryTokenStreamReader to read from the specified input buffer.
        /// </summary>
        /// <param name="buff">ArraySegment to use for the data.</param>
        public BinaryTokenStreamReader(ArraySegment<byte> buff)
            : this(new[] { buff })
        {
        }

        /// <summary> Current read position in the stream. </summary>
        public int CurrentPosition => currentOffset + totalProcessedBytes - currentSegmentOffset;

        /// <summary>
        /// Creates a copy of the current stream reader.
        /// </summary>
        /// <returns>The new copy</returns>
        public BinaryTokenStreamReader Copy()
        {
            return new BinaryTokenStreamReader(this.buffers);
        }

        private void StartNextSegment()
        {
            totalProcessedBytes += currentSegment.Count;
            currentSegmentIndex++;
            if (currentSegmentIndex < buffersCount)
            {
                InitializeCurrentSegment(currentSegmentIndex);
            }
            else
            {
                currentSegment = emptySegment;
                currentBuffer = null;
                currentOffset = 0;
                currentSegmentOffset = 0;
                currentSegmentOffsetPlusCount = currentSegmentOffset + currentSegmentCount;
            }
        }

        private byte[] CheckLength(int n, out int offset)
        {
            bool ignore;
            byte[] res;
            if (TryCheckLengthFast(n, out res, out offset, out ignore))
            {
                return res;
            }

            return CheckLength(n, out offset, out ignore);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryCheckLengthFast(int n, out byte[] res, out int offset, out bool safeToUse)
        {
            safeToUse = false;
            res = null;
            offset = 0;
            var nextOffset = currentOffset + n;
            if (nextOffset <= currentSegmentOffsetPlusCount)
            {
                offset = currentOffset;
                currentOffset = nextOffset;
                res = currentBuffer;
                return true;
            }

            return false;
        }

        private byte[] CheckLength(int n, out int offset, out bool safeToUse)
        {
            safeToUse = false;
            offset = 0;
            if (currentOffset == currentSegmentOffsetPlusCount)
            {
                StartNextSegment();
            }

            byte[] res;
            if (TryCheckLengthFast(n, out res, out offset, out safeToUse))
            {
                return res;
            }

            if ((CurrentPosition + n > totalLength))
            {
                throw new SerializationException(
                    String.Format("Attempt to read past the end of the input stream: CurrentPosition={0}, n={1}, totalLength={2}",
                    CurrentPosition, n, totalLength));
            }

            var temp = new byte[n];
            var i = 0;

            while (i < n)
            {
                var segmentOffsetPlusCount = currentSegmentOffsetPlusCount;
                var bytesFromThisBuffer = Math.Min(segmentOffsetPlusCount - currentOffset, n - i);
                Buffer.BlockCopy(currentBuffer, currentOffset, temp, i, bytesFromThisBuffer);
                i += bytesFromThisBuffer;
                currentOffset += bytesFromThisBuffer;
                if (currentOffset >= segmentOffsetPlusCount)
                {
                    if (currentSegmentIndex >= buffersCount)
                    {
                        throw new SerializationException(
                            String.Format("Attempt to read past buffers.Count: currentSegmentIndex={0}, buffers.Count={1}.", currentSegmentIndex, buffers.Count));
                    }

                    StartNextSegment();
                }
            }
            safeToUse = true;
            offset = 0;
            return temp;
        }

        /// <summary> Read an <c>Int32</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public int ReadInt()
        {
            int offset;
            var buff = CheckLength(sizeof(int), out offset);
            var val = BitConverter.ToInt32(buff, offset);
            Trace("--Read int {0}", val);
            return val;
        }

        /// <summary> Read an <c>UInt32</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public uint ReadUInt()
        {
            int offset;
            var buff = CheckLength(sizeof(uint), out offset);
            var val = BitConverter.ToUInt32(buff, offset);
            Trace("--Read uint {0}", val);
            return val;
        }

        /// <summary> Read an <c>Int16</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public short ReadShort()
        {
            int offset;
            var buff = CheckLength(sizeof(short), out offset);
            var val = BitConverter.ToInt16(buff, offset);
            Trace("--Read short {0}", val);
            return val;
        }

        /// <summary> Read an <c>UInt16</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public ushort ReadUShort()
        {
            int offset;
            var buff = CheckLength(sizeof(ushort), out offset);
            var val = BitConverter.ToUInt16(buff, offset);
            Trace("--Read ushort {0}", val);
            return val;
        }

        /// <summary> Read an <c>Int64</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public long ReadLong()
        {
            int offset;
            var buff = CheckLength(sizeof(long), out offset);
            var val = BitConverter.ToInt64(buff, offset);
            Trace("--Read long {0}", val);
            return val;
        }

        /// <summary> Read an <c>UInt64</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public ulong ReadULong()
        {
            int offset;
            var buff = CheckLength(sizeof(ulong), out offset);
            var val = BitConverter.ToUInt64(buff, offset);
            Trace("--Read ulong {0}", val);
            return val;
        }

        /// <summary> Read an <c>float</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public float ReadFloat()
        {
            int offset;
            var buff = CheckLength(sizeof(float), out offset);
            var val = BitConverter.ToSingle(buff, offset);
            Trace("--Read float {0}", val);
            return val;
        }

        /// <summary> Read an <c>double</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public double ReadDouble()
        {
            int offset;
            var buff = CheckLength(sizeof(double), out offset);
            var val = BitConverter.ToDouble(buff, offset);
            Trace("--Read double {0}", val);
            return val;
        }

        /// <summary> Read an <c>decimal</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public decimal ReadDecimal()
        {
            int offset;
            var buff = CheckLength(4 * sizeof(int), out offset);
            var raw = new int[4];
            Trace("--Read decimal");
            var n = offset;
            for (var i = 0; i < 4; i++)
            {
                raw[i] = BitConverter.ToInt32(buff, n);
                n += sizeof(int);
            }
            return new decimal(raw);
        }

        public DateTime ReadDateTime()
        {
            var n = ReadLong();
            return n == 0 ? default(DateTime) : DateTime.FromBinary(n);
        }

        /// <summary> Read an <c>string</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public string ReadString()
        {
            var n = ReadInt();
            if (n == 0)
            {
                Trace("--Read empty string");
                return String.Empty;
            }

            string s = null;
            // a length of -1 indicates that the string is null.
            if (-1 != n)
            {
                int offset;
                var buff = CheckLength(n, out offset);
                s = Encoding.UTF8.GetString(buff, offset, n);
            }

            Trace("--Read string '{0}'", s);
            return s;
        }

        /// <summary> Read the next bytes from the stream. </summary>
        /// <param name="count">Number of bytes to read.</param>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public byte[] ReadBytes(int count)
        {
            if (count == 0)
            {
                return emptyByteArray;
            }
            bool safeToUse;

            int offset;
            byte[] buff;
            if (!TryCheckLengthFast(count, out buff, out offset, out safeToUse))
            {
                buff = CheckLength(count, out offset, out safeToUse);
            }

            Trace("--Read byte array of length {0}", count);
            if (!safeToUse)
            {
                var result = new byte[count];
                Array.Copy(buff, offset, result, 0, count);
                return result;
            }
            else
            {
                return buff;
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

            var buffOffset = 0;
            var buff = count == 0 ? emptyByteArray : CheckLength(count, out buffOffset);
            Buffer.BlockCopy(buff, buffOffset, destination, offset, count);
        }

        /// <summary> Read an <c>char</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public char ReadChar()
        {
            Trace("--Read char");
            return Convert.ToChar(ReadShort());
        }

        /// <summary> Read an <c>byte</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public byte ReadByte()
        {
            int offset;
            var buff = CheckLength(1, out offset);
            Trace("--Read byte");
            return buff[offset];
        }

        /// <summary> Read an <c>sbyte</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public sbyte ReadSByte()
        {
            int offset;
            var buff = CheckLength(1, out offset);
            Trace("--Read sbyte");
            return unchecked((sbyte)(buff[offset]));
        }

        /// <summary> Read an <c>IPAddress</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public IPAddress ReadIPAddress()
        {
            int offset;
            var buff = CheckLength(16, out offset);
            bool v4 = true;
            for (var i = 0; i < 12; i++)
            {
                if (buff[offset + i] != 0)
                {
                    v4 = false;
                    break;
                }
            }

            if (v4)
            {
                var v4Bytes = new byte[4];
                for (var i = 0; i < 4; i++)
                {
                    v4Bytes[i] = buff[offset + 12 + i];
                }
                return new IPAddress(v4Bytes);
            }
            else
            {
                var v6Bytes = new byte[16];
                for (var i = 0; i < 16; i++)
                {
                    v6Bytes[i] = buff[offset + i];
                }
                return new IPAddress(v6Bytes);
            }
        }

        /// <summary> Read an <c>IPEndPoint</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public IPEndPoint ReadIPEndPoint()
        {
            var addr = ReadIPAddress();
            var port = ReadInt();
            return new IPEndPoint(addr, port);
        }

        /// <summary> Read an <c>SiloAddress</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public SiloAddress ReadSiloAddress()
        {
            var ep = ReadIPEndPoint();
            var gen = ReadInt();
            return SiloAddress.New(ep, gen);
        }

        /// <summary> Read an <c>GrainId</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        internal GrainId ReadGrainId()
        {
            UniqueKey key = ReadUniqueKey();
            return GrainId.GetGrainId(key);
        }

        /// <summary> Read an <c>ActivationId</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        internal ActivationId ReadActivationId()
        {
            UniqueKey key = ReadUniqueKey();
            return ActivationId.GetActivationId(key);
        }

        internal UniqueKey ReadUniqueKey()
        {
            ulong n0 = ReadULong();
            ulong n1 = ReadULong();
            ulong typeCodeData = ReadULong();
            string keyExt = ReadString();
            return UniqueKey.NewKey(n0, n1, typeCodeData, keyExt);
        }

        internal Guid ReadGuid()
        {
            byte[] bytes = ReadBytes(16);
            return new Guid(bytes);
        }

        public TimeSpan ReadTimeSpan()
        {
            return new TimeSpan(ReadLong());
        }

        internal CorrelationId ReadCorrelationId()
        {
            return new CorrelationId(ReadBytes(CorrelationId.SIZE_BYTES));
        }

        internal GrainDirectoryEntryStatus ReadMultiClusterStatus()
        {
            byte val = ReadByte();
            return (GrainDirectoryEntryStatus)val;
        }

        /// <summary> Read an <c>ActivationAddress</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        internal ActivationAddress ReadActivationAddress()
        {
            var silo = ReadSiloAddress();
            var grain = ReadGrainId();
            var act = ReadActivationId();

            if (silo.Equals(SiloAddress.Zero))
                silo = null;

            if (act.Equals(ActivationId.Zero))
                act = null;

            return ActivationAddress.GetAddress(silo, grain, act);
        }

        /// <summary>
        /// Read a block of data into the specified output <c>Array</c>.
        /// </summary>
        /// <param name="array">Array to output the data to.</param>
        /// <param name="n">Number of bytes to read.</param>
        public void ReadBlockInto(Array array, int n)
        {
            int offset;
            var buff = CheckLength(n, out offset);
            Buffer.BlockCopy(buff, offset, array, 0, n);
            Trace("--Read block of {0} bytes", n);
        }

        /// <summary>
        /// Peek at the next token in this input stream.
        /// </summary>
        /// <returns>Next token thatr will be read from the stream.</returns>
        internal SerializationTokenType PeekToken()
        {
            if (currentOffset == currentSegment.Count + currentSegment.Offset)
                StartNextSegment();

            return (SerializationTokenType)currentBuffer[currentOffset];
        }

        /// <summary> Read a <c>SerializationTokenType</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        internal SerializationTokenType ReadToken()
        {
            int offset;
            var buff = CheckLength(1, out offset);
            Trace("--Read token {0}", (SerializationTokenType)buff[offset]);
            return (SerializationTokenType)buff[offset];
        }

        internal bool TryReadSimpleType(out object result, out SerializationTokenType token)
        {
            token = ReadToken();
            byte[] bytes;
            switch (token)
            {
                case SerializationTokenType.True:
                    result = true;
                    break;
                case SerializationTokenType.False:
                    result = false;
                    break;
                case SerializationTokenType.Null:
                    result = null;
                    break;
                case SerializationTokenType.Object:
                    result = new object();
                    break;
                case SerializationTokenType.Int:
                    result = ReadInt();
                    break;
                case SerializationTokenType.Uint:
                    result = ReadUInt();
                    break;
                case SerializationTokenType.Short:
                    result = ReadShort();
                    break;
                case SerializationTokenType.Ushort:
                    result = ReadUShort();
                    break;
                case SerializationTokenType.Long:
                    result = ReadLong();
                    break;
                case SerializationTokenType.Ulong:
                    result = ReadULong();
                    break;
                case SerializationTokenType.Byte:
                    result = ReadByte();
                    break;
                case SerializationTokenType.Sbyte:
                    result = ReadSByte();
                    break;
                case SerializationTokenType.Float:
                    result = ReadFloat();
                    break;
                case SerializationTokenType.Double:
                    result = ReadDouble();
                    break;
                case SerializationTokenType.Decimal:
                    result = ReadDecimal();
                    break;
                case SerializationTokenType.String:
                    result = ReadString();
                    break;
                case SerializationTokenType.Character:
                    result = ReadChar();
                    break;
                case SerializationTokenType.Guid:
                    bytes = ReadBytes(16);
                    result = new Guid(bytes);
                    break;
                case SerializationTokenType.Date:
                    result = DateTime.FromBinary(ReadLong());
                    break;
                case SerializationTokenType.TimeSpan:
                    result = new TimeSpan(ReadLong());
                    break;
                case SerializationTokenType.GrainId:
                    result = ReadGrainId();
                    break;
                case SerializationTokenType.ActivationId:
                    result = ReadActivationId();
                    break;
                case SerializationTokenType.SiloAddress:
                    result = ReadSiloAddress();
                    break;
                case SerializationTokenType.ActivationAddress:
                    result = ReadActivationAddress();
                    break;
                case SerializationTokenType.IpAddress:
                    result = ReadIPAddress();
                    break;
                case SerializationTokenType.IpEndPoint:
                    result = ReadIPEndPoint();
                    break;
                case SerializationTokenType.CorrelationId:
                    result = new CorrelationId(ReadBytes(CorrelationId.SIZE_BYTES));
                    break;
                default:
                    result = null;
                    return false;
            }
            return true;
        }

        /// <summary> Read a <c>Type</c> value from the stream. </summary>
        /// <param name="expected">Expected Type, if known.</param>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        public Type ReadFullTypeHeader(Type expected = null)
        {
            var token = ReadToken();

            if (token == SerializationTokenType.ExpectedType)
            {
                return expected;
            }

            var t = CheckSpecialTypeCode(token);
            if (t != null)
            {
                return t;
            }

            if (token == SerializationTokenType.SpecifiedType)
            {
#if TRACE_SERIALIZATION
                var tt = ReadSpecifiedTypeHeader();
                Trace("--Read specified type header for type {0}", tt);
                return tt;
#else
                return ReadSpecifiedTypeHeader();
#endif
            }

            throw new SerializationException("Invalid '" + token + "'token in input stream where full type header is expected");
        }

        internal static Type CheckSpecialTypeCode(SerializationTokenType token)
        {
            switch (token)
            {
                case SerializationTokenType.Boolean:
                    return typeof(bool);
                case SerializationTokenType.Int:
                    return typeof(int);
                case SerializationTokenType.Short:
                    return typeof(short);
                case SerializationTokenType.Long:
                    return typeof(long);
                case SerializationTokenType.Sbyte:
                    return typeof(sbyte);
                case SerializationTokenType.Uint:
                    return typeof(uint);
                case SerializationTokenType.Ushort:
                    return typeof(ushort);
                case SerializationTokenType.Ulong:
                    return typeof(ulong);
                case SerializationTokenType.Byte:
                    return typeof(byte);
                case SerializationTokenType.Float:
                    return typeof(float);
                case SerializationTokenType.Double:
                    return typeof(double);
                case SerializationTokenType.Decimal:
                    return typeof(decimal);
                case SerializationTokenType.String:
                    return typeof(string);
                case SerializationTokenType.Character:
                    return typeof(char);
                case SerializationTokenType.Guid:
                    return typeof(Guid);
                case SerializationTokenType.Date:
                    return typeof(DateTime);
                case SerializationTokenType.TimeSpan:
                    return typeof(TimeSpan);
                case SerializationTokenType.IpAddress:
                    return typeof(IPAddress);
                case SerializationTokenType.IpEndPoint:
                    return typeof(IPEndPoint);
                case SerializationTokenType.GrainId:
                    return typeof(GrainId);
                case SerializationTokenType.ActivationId:
                    return typeof(ActivationId);
                case SerializationTokenType.SiloAddress:
                    return typeof(SiloAddress);
                case SerializationTokenType.ActivationAddress:
                    return typeof(ActivationAddress);
                case SerializationTokenType.CorrelationId:
                    return typeof(CorrelationId);
#if false // Note: not yet implemented as simple types on the Writer side
                case SerializationTokenType.Object:
                    return typeof(Object);
                case SerializationTokenType.ByteArray:
                    return typeof(byte[]);
                case SerializationTokenType.ShortArray:
                    return typeof(short[]);
                case SerializationTokenType.IntArray:
                    return typeof(int[]);
                case SerializationTokenType.LongArray:
                    return typeof(long[]);
                case SerializationTokenType.UShortArray:
                    return typeof(ushort[]);
                case SerializationTokenType.UIntArray:
                    return typeof(uint[]);
                case SerializationTokenType.ULongArray:
                    return typeof(ulong[]);
                case SerializationTokenType.FloatArray:
                    return typeof(float[]);
                case SerializationTokenType.DoubleArray:
                    return typeof(double[]);
                case SerializationTokenType.CharArray:
                    return typeof(char[]);
                case SerializationTokenType.BoolArray:
                    return typeof(bool[]);
#endif
                default:
                    break;
            }

            return null;
        }

        /// <summary> Read a <c>Type</c> value from the stream. </summary>
        internal Type ReadSpecifiedTypeHeader()
        {
            // Assumes that the SpecifiedType token has already been read

            var token = ReadToken();
            switch (token)
            {
                case SerializationTokenType.Boolean:
                    return typeof(bool);
                case SerializationTokenType.Int:
                    return typeof(int);
                case SerializationTokenType.Short:
                    return typeof(short);
                case SerializationTokenType.Long:
                    return typeof(long);
                case SerializationTokenType.Sbyte:
                    return typeof(sbyte);
                case SerializationTokenType.Uint:
                    return typeof(uint);
                case SerializationTokenType.Ushort:
                    return typeof(ushort);
                case SerializationTokenType.Ulong:
                    return typeof(ulong);
                case SerializationTokenType.Byte:
                    return typeof(byte);
                case SerializationTokenType.Float:
                    return typeof(float);
                case SerializationTokenType.Double:
                    return typeof(double);
                case SerializationTokenType.Decimal:
                    return typeof(decimal);
                case SerializationTokenType.String:
                    return typeof(string);
                case SerializationTokenType.Character:
                    return typeof(char);
                case SerializationTokenType.Guid:
                    return typeof(Guid);
                case SerializationTokenType.Date:
                    return typeof(DateTime);
                case SerializationTokenType.TimeSpan:
                    return typeof(TimeSpan);
                case SerializationTokenType.IpAddress:
                    return typeof(IPAddress);
                case SerializationTokenType.IpEndPoint:
                    return typeof(IPEndPoint);
                case SerializationTokenType.GrainId:
                    return typeof(GrainId);
                case SerializationTokenType.ActivationId:
                    return typeof(ActivationId);
                case SerializationTokenType.SiloAddress:
                    return typeof(SiloAddress);
                case SerializationTokenType.ActivationAddress:
                    return typeof(ActivationAddress);
                case SerializationTokenType.CorrelationId:
                    return typeof(CorrelationId);
                case SerializationTokenType.Request:
                    return typeof(InvokeMethodRequest);
                case SerializationTokenType.Response:
                    return typeof(Response);
                case SerializationTokenType.StringObjDict:
                    return typeof(Dictionary<string, object>);
                case SerializationTokenType.Object:
                    return typeof(Object);
                case SerializationTokenType.Tuple + 1:
                    Trace("----Reading type info for a Tuple'1");
                    return typeof(Tuple<>).MakeGenericType(ReadGenericArguments(1));
                case SerializationTokenType.Tuple + 2:
                    Trace("----Reading type info for a Tuple'2");
                    return typeof(Tuple<,>).MakeGenericType(ReadGenericArguments(2));
                case SerializationTokenType.Tuple + 3:
                    Trace("----Reading type info for a Tuple'3");
                    return typeof(Tuple<,,>).MakeGenericType(ReadGenericArguments(3));
                case SerializationTokenType.Tuple + 4:
                    Trace("----Reading type info for a Tuple'4");
                    return typeof(Tuple<,,,>).MakeGenericType(ReadGenericArguments(4));
                case SerializationTokenType.Tuple + 5:
                    Trace("----Reading type info for a Tuple'5");
                    return typeof(Tuple<,,,,>).MakeGenericType(ReadGenericArguments(5));
                case SerializationTokenType.Tuple + 6:
                    Trace("----Reading type info for a Tuple'6");
                    return typeof(Tuple<,,,,,>).MakeGenericType(ReadGenericArguments(6));
                case SerializationTokenType.Tuple + 7:
                    Trace("----Reading type info for a Tuple'7");
                    return typeof(Tuple<,,,,,,>).MakeGenericType(ReadGenericArguments(7));
                case SerializationTokenType.Array + 1:
                    var et1 = ReadFullTypeHeader();
                    return et1.MakeArrayType();
                case SerializationTokenType.Array + 2:
                    var et2 = ReadFullTypeHeader();
                    return et2.MakeArrayType(2);
                case SerializationTokenType.Array + 3:
                    var et3 = ReadFullTypeHeader();
                    return et3.MakeArrayType(3);
                case SerializationTokenType.Array + 4:
                    var et4 = ReadFullTypeHeader();
                    return et4.MakeArrayType(4);
                case SerializationTokenType.Array + 5:
                    var et5 = ReadFullTypeHeader();
                    return et5.MakeArrayType(5);
                case SerializationTokenType.Array + 6:
                    var et6 = ReadFullTypeHeader();
                    return et6.MakeArrayType(6);
                case SerializationTokenType.Array + 7:
                    var et7 = ReadFullTypeHeader();
                    return et7.MakeArrayType(7);
                case SerializationTokenType.Array + 8:
                    var et8 = ReadFullTypeHeader();
                    return et8.MakeArrayType(8);
                case SerializationTokenType.List:
                    return typeof(List<>).MakeGenericType(ReadGenericArguments(1));
                case SerializationTokenType.Dictionary:
                    return typeof(Dictionary<,>).MakeGenericType(ReadGenericArguments(2));
                case SerializationTokenType.KeyValuePair:
                    return typeof(KeyValuePair<,>).MakeGenericType(ReadGenericArguments(2));
                case SerializationTokenType.Set:
                    return typeof(HashSet<>).MakeGenericType(ReadGenericArguments(1));
                case SerializationTokenType.SortedList:
                    return typeof(SortedList<,>).MakeGenericType(ReadGenericArguments(2));
                case SerializationTokenType.SortedSet:
                    return typeof(SortedSet<>).MakeGenericType(ReadGenericArguments(1));
                case SerializationTokenType.Stack:
                    return typeof(Stack<>).MakeGenericType(ReadGenericArguments(1));
                case SerializationTokenType.Queue:
                    return typeof(Queue<>).MakeGenericType(ReadGenericArguments(1));
                case SerializationTokenType.LinkedList:
                    return typeof(LinkedList<>).MakeGenericType(ReadGenericArguments(1));
                case SerializationTokenType.Nullable:
                    return typeof(Nullable<>).MakeGenericType(ReadGenericArguments(1));
                case SerializationTokenType.ByteArray:
                    return typeof(byte[]);
                case SerializationTokenType.ShortArray:
                    return typeof(short[]);
                case SerializationTokenType.IntArray:
                    return typeof(int[]);
                case SerializationTokenType.LongArray:
                    return typeof(long[]);
                case SerializationTokenType.UShortArray:
                    return typeof(ushort[]);
                case SerializationTokenType.UIntArray:
                    return typeof(uint[]);
                case SerializationTokenType.ULongArray:
                    return typeof(ulong[]);
                case SerializationTokenType.FloatArray:
                    return typeof(float[]);
                case SerializationTokenType.DoubleArray:
                    return typeof(double[]);
                case SerializationTokenType.CharArray:
                    return typeof(char[]);
                case SerializationTokenType.BoolArray:
                    return typeof(bool[]);
                case SerializationTokenType.SByteArray:
                    return typeof(sbyte[]);
                case SerializationTokenType.NamedType:
                    var typeName = ReadString();
                    try
                    {
                        return SerializationManager.ResolveTypeName(typeName);
                    }
                    catch (TypeAccessException ex)
                    {
                        throw new TypeAccessException("Named type \"" + typeName + "\" is invalid: " + ex.Message);
                    }
                default:
                    break;
            }

            throw new SerializationException("Unexpected '" + token + "' found when expecting a type reference");
        }

        private Type[] ReadGenericArguments(int n)
        {
            Trace("About to read {0} generic arguments", n);
            var args = new Type[n];
            for (var i = 0; i < n; i++)
            {
                args[i] = ReadFullTypeHeader();
            }
            Trace("Finished reading {0} generic arguments", n);
            return args;
        }

        private StreamWriter trace;

        [Conditional("TRACE_SERIALIZATION")]
        private void Trace(string format, params object[] args)
        {
            if (trace == null)
            {
                var path = String.Format("d:\\Trace-{0}.{1}.{2}.txt", DateTime.UtcNow.Hour, DateTime.UtcNow.Minute, DateTime.UtcNow.Ticks);
                Console.WriteLine("Opening trace file at '{0}'", path);
                trace = File.CreateText(path);
            }
            trace.Write(format, args);
            trace.WriteLine(" at offset {0}", CurrentPosition);
            trace.Flush();
        }
    }
}

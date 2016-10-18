using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Orleans.CodeGeneration;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    /// <summary>
    /// Writer for Orleans binary token streams
    /// </summary>
    public class BinaryTokenStreamWriter
    {
        private readonly ByteArrayBuilder ab;

        private static readonly Dictionary<RuntimeTypeHandle, SerializationTokenType> typeTokens;

        private static readonly Dictionary<RuntimeTypeHandle, Action<BinaryTokenStreamWriter, object>> writers;

        static BinaryTokenStreamWriter()
        {
            typeTokens = new Dictionary<RuntimeTypeHandle, SerializationTokenType>();
            typeTokens[typeof(bool).TypeHandle] = SerializationTokenType.Boolean;
            typeTokens[typeof(int).TypeHandle] = SerializationTokenType.Int;
            typeTokens[typeof(uint).TypeHandle] = SerializationTokenType.Uint;
            typeTokens[typeof(short).TypeHandle] = SerializationTokenType.Short;
            typeTokens[typeof(ushort).TypeHandle] = SerializationTokenType.Ushort;
            typeTokens[typeof(long).TypeHandle] = SerializationTokenType.Long;
            typeTokens[typeof(ulong).TypeHandle] = SerializationTokenType.Ulong;
            typeTokens[typeof(byte).TypeHandle] = SerializationTokenType.Byte;
            typeTokens[typeof(sbyte).TypeHandle] = SerializationTokenType.Sbyte;
            typeTokens[typeof(float).TypeHandle] = SerializationTokenType.Float;
            typeTokens[typeof(double).TypeHandle] = SerializationTokenType.Double;
            typeTokens[typeof(decimal).TypeHandle] = SerializationTokenType.Decimal;
            typeTokens[typeof(string).TypeHandle] = SerializationTokenType.String;
            typeTokens[typeof(char).TypeHandle] = SerializationTokenType.Character;
            typeTokens[typeof(Guid).TypeHandle] = SerializationTokenType.Guid;
            typeTokens[typeof(DateTime).TypeHandle] = SerializationTokenType.Date;
            typeTokens[typeof(TimeSpan).TypeHandle] = SerializationTokenType.TimeSpan;
            typeTokens[typeof(GrainId).TypeHandle] = SerializationTokenType.GrainId;
            typeTokens[typeof(ActivationId).TypeHandle] = SerializationTokenType.ActivationId;
            typeTokens[typeof(SiloAddress).TypeHandle] = SerializationTokenType.SiloAddress;
            typeTokens[typeof(ActivationAddress).TypeHandle] = SerializationTokenType.ActivationAddress;
            typeTokens[typeof(IPAddress).TypeHandle] = SerializationTokenType.IpAddress;
            typeTokens[typeof(IPEndPoint).TypeHandle] = SerializationTokenType.IpEndPoint;
            typeTokens[typeof(CorrelationId).TypeHandle] = SerializationTokenType.CorrelationId;
            typeTokens[typeof(InvokeMethodRequest).TypeHandle] = SerializationTokenType.Request;
            typeTokens[typeof(Response).TypeHandle] = SerializationTokenType.Response;
            typeTokens[typeof(Dictionary<string, object>).TypeHandle] = SerializationTokenType.StringObjDict;
            typeTokens[typeof(Object).TypeHandle] = SerializationTokenType.Object;
            typeTokens[typeof(List<>).TypeHandle] = SerializationTokenType.List;
            typeTokens[typeof(SortedList<,>).TypeHandle] = SerializationTokenType.SortedList;
            typeTokens[typeof(Dictionary<,>).TypeHandle] = SerializationTokenType.Dictionary;
            typeTokens[typeof(HashSet<>).TypeHandle] = SerializationTokenType.Set;
            typeTokens[typeof(SortedSet<>).TypeHandle] = SerializationTokenType.SortedSet;
            typeTokens[typeof(KeyValuePair<,>).TypeHandle] = SerializationTokenType.KeyValuePair;
            typeTokens[typeof(LinkedList<>).TypeHandle] = SerializationTokenType.LinkedList;
            typeTokens[typeof(Stack<>).TypeHandle] = SerializationTokenType.Stack;
            typeTokens[typeof(Queue<>).TypeHandle] = SerializationTokenType.Queue;
            typeTokens[typeof(Tuple<>).TypeHandle] = SerializationTokenType.Tuple + 1;
            typeTokens[typeof(Tuple<,>).TypeHandle] = SerializationTokenType.Tuple + 2;
            typeTokens[typeof(Tuple<,,>).TypeHandle] = SerializationTokenType.Tuple + 3;
            typeTokens[typeof(Tuple<,,,>).TypeHandle] = SerializationTokenType.Tuple + 4;
            typeTokens[typeof(Tuple<,,,,>).TypeHandle] = SerializationTokenType.Tuple + 5;
            typeTokens[typeof(Tuple<,,,,,>).TypeHandle] = SerializationTokenType.Tuple + 6;
            typeTokens[typeof(Tuple<,,,,,,>).TypeHandle] = SerializationTokenType.Tuple + 7;

            writers = new Dictionary<RuntimeTypeHandle, Action<BinaryTokenStreamWriter, object>>();
            writers[typeof(bool).TypeHandle] = (stream, obj) => stream.Write((bool) obj);
            writers[typeof(int).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Int); stream.Write((int) obj); };
            writers[typeof(uint).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Uint); stream.Write((uint) obj); };
            writers[typeof(short).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Short); stream.Write((short) obj); };
            writers[typeof(ushort).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Ushort); stream.Write((ushort) obj); };
            writers[typeof(long).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Long); stream.Write((long) obj); };
            writers[typeof(ulong).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Ulong); stream.Write((ulong) obj); };
            writers[typeof(byte).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Byte); stream.Write((byte) obj); };
            writers[typeof(sbyte).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Sbyte); stream.Write((sbyte) obj); };
            writers[typeof(float).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Float); stream.Write((float) obj); };
            writers[typeof(double).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Double); stream.Write((double) obj); };
            writers[typeof(decimal).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Decimal); stream.Write((decimal)obj); };
            writers[typeof(string).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.String); stream.Write((string)obj); };
            writers[typeof(char).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Character); stream.Write((char) obj); };
            writers[typeof(Guid).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Guid); stream.Write((Guid) obj); };
            writers[typeof(DateTime).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Date); stream.Write((DateTime) obj); };
            writers[typeof(TimeSpan).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.TimeSpan); stream.Write((TimeSpan) obj); };
            writers[typeof(GrainId).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.GrainId); stream.Write((GrainId) obj); };
            writers[typeof(ActivationId).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.ActivationId); stream.Write((ActivationId) obj); };
            writers[typeof(SiloAddress).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.SiloAddress); stream.Write((SiloAddress) obj); };
            writers[typeof(ActivationAddress).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.ActivationAddress); stream.Write((ActivationAddress) obj); };
            writers[typeof(IPAddress).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.IpAddress); stream.Write((IPAddress) obj); };
            writers[typeof(IPEndPoint).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.IpEndPoint); stream.Write((IPEndPoint) obj); };
            writers[typeof(CorrelationId).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.CorrelationId); stream.Write((CorrelationId) obj); };
        }

        /// <summary> Default constructor. </summary>
        public BinaryTokenStreamWriter()
        {
            ab = new ByteArrayBuilder();
            Trace("Starting new binary token stream");
        }

        /// <summary> Return the output stream as a set of <c>ArraySegment</c>. </summary>
        /// <returns>Data from this stream, converted to output type.</returns>
        public IList<ArraySegment<byte>> ToBytes()
        {
            return ab.ToBytes();
        }

        /// <summary> Return the output stream as a <c>byte[]</c>. </summary>
        /// <returns>Data from this stream, converted to output type.</returns>
        public byte[] ToByteArray()
        {
            return ab.ToByteArray();
        }

        /// <summary> Release any serialization buffers being used by this stream. </summary>
        public void ReleaseBuffers()
        {
            ab.ReleaseBuffers();
        }

        /// <summary> Current write position in the stream. </summary>
        public int CurrentOffset { get { return ab.Length; } }

        // Numbers


        /// <summary> Write an <c>Int32</c> value to the stream. </summary>
        public void Write(int i)
        {
            Trace("--Wrote integer {0}", i);
            ab.Append(i);
        }

        /// <summary> Write an <c>Int16</c> value to the stream. </summary>
        public void Write(short s)
        {
            Trace("--Wrote short {0}", s);
            ab.Append(s);
        }

        /// <summary> Write an <c>Int64</c> value to the stream. </summary>
        public void Write(long l)
        {
            Trace("--Wrote long {0}", l);
            ab.Append(l);
        }

        /// <summary> Write a <c>sbyte</c> value to the stream. </summary>
        public void Write(sbyte b)
        {
            Trace("--Wrote sbyte {0}", b);
            ab.Append(b);
        }

        /// <summary> Write a <c>UInt32</c> value to the stream. </summary>
        public void Write(uint u)
        {
            Trace("--Wrote uint {0}", u);
            ab.Append(u);
        }

        /// <summary> Write a <c>UInt16</c> value to the stream. </summary>
        public void Write(ushort u)
        {
            Trace("--Wrote ushort {0}", u);
            ab.Append(u);
        }

        /// <summary> Write a <c>UInt64</c> value to the stream. </summary>
        public void Write(ulong u)
        {
            Trace("--Wrote ulong {0}", u);
            ab.Append(u);
        }

        /// <summary> Write a <c>byte</c> value to the stream. </summary>
        public void Write(byte b)
        {
            Trace("--Wrote byte {0}", b);
            ab.Append(b);
        }

        /// <summary> Write a <c>float</c> value to the stream. </summary>
        public void Write(float f)
        {
            Trace("--Wrote float {0}", f);
            ab.Append(f);
        }

        /// <summary> Write a <c>double</c> value to the stream. </summary>
        public void Write(double d)
        {
            Trace("--Wrote double {0}", d);
            ab.Append(d);
        }

        /// <summary> Write a <c>decimal</c> value to the stream. </summary>
        public void Write(decimal d)
        {
            Trace("--Wrote decimal {0}", d);
            ab.Append(Decimal.GetBits(d));
        }

        // Text

        /// <summary> Write a <c>string</c> value to the stream. </summary>
        public void Write(string s)
        {
            Trace("--Wrote string '{0}'", s);
            if (null == s)
            {
                ab.Append(-1);
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(s);
                ab.Append(bytes.Length);
                ab.Append(bytes);
            }
        }

        /// <summary> Write a <c>char</c> value to the stream. </summary>
        public void Write(char c)
        {
            Trace("--Wrote char {0}", c);
            ab.Append(Convert.ToInt16(c));
        }

        // Other primitives

        /// <summary> Write a <c>bool</c> value to the stream. </summary>
        public void Write(bool b)
        {
            Trace("--Wrote Boolean {0}", b);
            ab.Append((byte)(b ? SerializationTokenType.True : SerializationTokenType.False));
        }

        /// <summary> Write a <c>null</c> value to the stream. </summary>
        public void WriteNull()
        {
            Trace("--Wrote null");
            ab.Append((byte)SerializationTokenType.Null);
        }

        internal void Write(SerializationTokenType t)
        {
            Trace("--Wrote token {0}", t);
            ab.Append((byte)t);
        }

        // Types

        /// <summary> Write a type header for the specified Type to the stream. </summary>
        /// <param name="t">Type to write header for.</param>
        /// <param name="expected">Currently expected Type for this stream.</param>
        public void WriteTypeHeader(Type t, Type expected = null)
        {
            Trace("-Writing type header for type {0}, expected {1}", t, expected);
            if (t == expected)
            {
                ab.Append((byte)SerializationTokenType.ExpectedType);
                return;
            }

            ab.Append((byte) SerializationTokenType.SpecifiedType);

            if (t.IsArray)
            {
                ab.Append((byte)(SerializationTokenType.Array + (byte)t.GetArrayRank()));
                WriteTypeHeader(t.GetElementType());
                return;
            }

            SerializationTokenType token;
            if (typeTokens.TryGetValue(t.TypeHandle, out token))
            {
                ab.Append((byte) token);
                return;
            }

            if (t.GetTypeInfo().IsGenericType)
            {
                if (typeTokens.TryGetValue(t.GetGenericTypeDefinition().TypeHandle, out token))
                {
                    ab.Append((byte)token);
                    foreach (var tp in t.GetGenericArguments())
                    {
                        WriteTypeHeader(tp);
                    }
                    return;
                }                
            }

            ab.Append((byte)SerializationTokenType.NamedType);
            var typeKey = t.OrleansTypeKey();
            ab.Append(typeKey.Length);
            ab.Append(typeKey);
        }

        // Primitive arrays

        /// <summary> Write a <c>byte[]</c> value to the stream. </summary>
        public void Write(byte[] b)
        {
            Trace("--Wrote byte array of length {0}", b.Length);
            ab.Append(b);
        }

        /// <summary> Write the specified number of bytes to the stream, starting at the specified offset in the input <c>byte[]</c>. </summary>
        /// <param name="b">The input data to be written.</param>
        /// <param name="offset">The offset into the inout byte[] to start writing bytes from.</param>
        /// <param name="count">The number of bytes to be written.</param>
        public void Write(byte[] b, int offset, int count)
        {
            if (count <= 0)
            {
                return;
            }
            Trace("--Wrote byte array of length {0}", count);
            if ((offset == 0) && (count == b.Length))
            {
                Write(b);
            }
            else
            {
                var temp = new byte[count];
                Buffer.BlockCopy(b, offset, temp, 0, count);
                Write(temp);
            }
        }

        /// <summary> Write a <c>Int16[]</c> value to the stream. </summary>
        public void Write(short[] i)
        {
            Trace("--Wrote short array of length {0}", i.Length);
            ab.Append(i);
        }

        /// <summary> Write a <c>Int32[]</c> value to the stream. </summary>
        public void Write(int[] i)
        {
            Trace("--Wrote short array of length {0}", i.Length);
            ab.Append(i);
        }

        /// <summary> Write a <c>Int64[]</c> value to the stream. </summary>
        public void Write(long[] l)
        {
            Trace("--Wrote long array of length {0}", l.Length);
            ab.Append(l);
        }

        /// <summary> Write a <c>UInt16[]</c> value to the stream. </summary>
        public void Write(ushort[] i)
        {
            Trace("--Wrote ushort array of length {0}", i.Length);
            ab.Append(i);
        }

        /// <summary> Write a <c>UInt32[]</c> value to the stream. </summary>
        public void Write(uint[] i)
        {
            Trace("--Wrote uint array of length {0}", i.Length);
            ab.Append(i);
        }

        /// <summary> Write a <c>UInt64[]</c> value to the stream. </summary>
        public void Write(ulong[] l)
        {
            Trace("--Wrote ulong array of length {0}", l.Length);
            ab.Append(l);
        }

        /// <summary> Write a <c>sbyte[]</c> value to the stream. </summary>
        public void Write(sbyte[] l)
        {
            Trace("--Wrote sbyte array of length {0}", l.Length);
            ab.Append(l);
        }

        /// <summary> Write a <c>char[]</c> value to the stream. </summary>
        public void Write(char[] l)
        {
            Trace("--Wrote char array of length {0}", l.Length);
            ab.Append(l);
        }

        /// <summary> Write a <c>bool[]</c> value to the stream. </summary>
        public void Write(bool[] l)
        {
            Trace("--Wrote bool array of length {0}", l.Length);
            ab.Append(l);
        }

        /// <summary> Write a <c>double[]</c> value to the stream. </summary>
        public void Write(double[] d)
        {
            Trace("--Wrote double array of length {0}", d.Length);
            ab.Append(d);
        }

        /// <summary> Write a <c>float[]</c> value to the stream. </summary>
        public void Write(float[] f)
        {
            Trace("--Wrote float array of length {0}", f.Length);
            ab.Append(f);
        }

        // Other simple types

        /// <summary> Write a <c>CorrelationId</c> value to the stream. </summary>
        internal void Write(CorrelationId id)
        {
            Write(id.ToByteArray());
        }

        /// <summary> Write a <c>IPEndPoint</c> value to the stream. </summary>
        public void Write(IPEndPoint ep)
        {
            Write(ep.Address);
            Write(ep.Port);
        }

        /// <summary> Write a <c>IPAddress</c> value to the stream. </summary>
        public void Write(IPAddress ip)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                for (var i = 0; i < 12; i++)
                {
                    Write((byte)0);
                }
                Write(ip.GetAddressBytes()); // IPv4 -- 4 bytes
            }
            else
            {
                Write(ip.GetAddressBytes()); // IPv6 -- 16 bytes
            }
        }

        /// <summary> Write a <c>ActivationAddress</c> value to the stream. </summary>
        internal void Write(ActivationAddress addr)
        {
            Write(addr.Silo ?? SiloAddress.Zero);

            // GrainId must not be null
            Write(addr.Grain);
            Write(addr.Activation ?? ActivationId.Zero);
        }

        /// <summary> Write a <c>SiloAddress</c> value to the stream. </summary>
        public void Write(SiloAddress addr)
        {
            Write(addr.Endpoint);
            Write(addr.Generation);
        }

        internal void Write(UniqueKey key)
        {
            Write(key.N0);
            Write(key.N1);
            Write(key.TypeCodeData);
            Write(key.KeyExt);
        }

        /// <summary> Write a <c>ActivationId</c> value to the stream. </summary>
        internal void Write(ActivationId id)
        {
            Write(id.Key);
        }

        /// <summary> Write a <c>GrainId</c> value to the stream. </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters")]
        internal void Write(GrainId id)
        {
            Write(id.Key);
        }

        /// <summary> Write a <c>TimeSpan</c> value to the stream. </summary>
        public void Write(TimeSpan ts)
        {
            Write(ts.Ticks);
        }

        /// <summary> Write a <c>DataTime</c> value to the stream. </summary>
        public void Write(DateTime dt)
        {
            Write(dt.ToBinary());
        }

        /// <summary> Write a <c>Guid</c> value to the stream. </summary>
        public void Write(Guid id)
        {
            Write(id.ToByteArray());
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
                WriteNull();
                return true;
            }
            Action<BinaryTokenStreamWriter, object> writer;
            if (writers.TryGetValue(obj.GetType().TypeHandle, out writer))
            {
                writer(this, obj);
                return true;
            }
            return false;
        }

        // General containers

        /// <summary>
        /// Write header for an <c>Array</c> to the output stream.
        /// </summary>
        /// <param name="a">Data object for which header should be written.</param>
        /// <param name="expected">The most recent Expected Type currently active for this stream.</param>
        internal void WriteArrayHeader(Array a, Type expected = null)
        {
            WriteTypeHeader(a.GetType(), expected);
            for (var i = 0; i < a.Rank; i++)
            {
                ab.Append(a.GetLength(i));
            }
        }

        // Back-references

        internal void WriteReference(int offset)
        {
            Trace("Writing a reference to the object at offset {0}", offset);
            ab.Append((byte) SerializationTokenType.Reference);
            ab.Append(offset);
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
            trace.WriteLine(" at offset {0}", CurrentOffset);
            trace.Flush();
        }
    }
}

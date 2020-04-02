using System;
using System.Collections.Generic;
using System.Net;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    public interface IBinaryTokenStreamWriter
    {
        /// <summary> Current write position in the stream. </summary>
        int CurrentOffset { get; }

        /// <summary> Write an <c>Int32</c> value to the stream. </summary>
        void Write(int i);

        /// <summary> Write an <c>Int16</c> value to the stream. </summary>
        void Write(short s);

        /// <summary> Write an <c>Int64</c> value to the stream. </summary>
        void Write(long l);

        /// <summary> Write a <c>sbyte</c> value to the stream. </summary>
        void Write(sbyte b);

        /// <summary> Write a <c>UInt32</c> value to the stream. </summary>
        void Write(uint u);

        /// <summary> Write a <c>UInt16</c> value to the stream. </summary>
        void Write(ushort u);

        /// <summary> Write a <c>UInt64</c> value to the stream. </summary>
        void Write(ulong u);

        /// <summary> Write a <c>byte</c> value to the stream. </summary>
        void Write(byte b);

        /// <summary> Write a <c>float</c> value to the stream. </summary>
        void Write(float f);

        /// <summary> Write a <c>double</c> value to the stream. </summary>
        void Write(double d);

        /// <summary> Write a <c>decimal</c> value to the stream. </summary>
        void Write(decimal d);

        /// <summary> Write a <c>string</c> value to the stream. </summary>
        void Write(string s);

        /// <summary> Write a <c>char</c> value to the stream. </summary>
        void Write(char c);

        /// <summary> Write a <c>bool</c> value to the stream. </summary>
        void Write(bool b);

        /// <summary> Write a <c>null</c> value to the stream. </summary>
        void WriteNull();

        /// <summary> Write a type header for the specified Type to the stream. </summary>
        /// <param name="t">Type to write header for.</param>
        /// <param name="expected">Currently expected Type for this stream.</param>
        void WriteTypeHeader(Type t, Type expected = null);

        /// <summary> Write a <c>byte[]</c> value to the stream. </summary>
        void Write(byte[] b);

        /// <summary> Write a list of byte array segments to the stream. </summary>
        void Write(List<ArraySegment<byte>> bytes);

        /// <summary> Write the specified number of bytes to the stream, starting at the specified offset in the input <c>byte[]</c>. </summary>
        /// <param name="b">The input data to be written.</param>
        /// <param name="offset">The offset into the inout byte[] to start writing bytes from.</param>
        /// <param name="count">The number of bytes to be written.</param>
        void Write(byte[] b, int offset, int count);

        /// <summary> Write a <c>Int16[]</c> value to the stream. </summary>
        void Write(short[] i);

        /// <summary> Write a <c>Int32[]</c> value to the stream. </summary>
        void Write(int[] i);

        /// <summary> Write a <c>Int64[]</c> value to the stream. </summary>
        void Write(long[] l);

        /// <summary> Write a <c>UInt16[]</c> value to the stream. </summary>
        void Write(ushort[] i);

        /// <summary> Write a <c>UInt32[]</c> value to the stream. </summary>
        void Write(uint[] i);

        /// <summary> Write a <c>UInt64[]</c> value to the stream. </summary>
        void Write(ulong[] l);

        /// <summary> Write a <c>sbyte[]</c> value to the stream. </summary>
        void Write(sbyte[] l);

        /// <summary> Write a <c>char[]</c> value to the stream. </summary>
        void Write(char[] l);

        /// <summary> Write a <c>bool[]</c> value to the stream. </summary>
        void Write(bool[] l);

        /// <summary> Write a <c>double[]</c> value to the stream. </summary>
        void Write(double[] d);

        /// <summary> Write a <c>float[]</c> value to the stream. </summary>
        void Write(float[] f);

        /// <summary> Write a <c>IPEndPoint</c> value to the stream. </summary>
        void Write(IPEndPoint ep);

        /// <summary> Write a <c>IPAddress</c> value to the stream. </summary>
        void Write(IPAddress ip);

        /// <summary> Write a <c>SiloAddress</c> value to the stream. </summary>
        void Write(SiloAddress addr);

        /// <summary> Write a <c>TimeSpan</c> value to the stream. </summary>
        void Write(TimeSpan ts);

        /// <summary> Write a <c>DataTime</c> value to the stream. </summary>
        void Write(DateTime dt);

        /// <summary> Write a <c>Guid</c> value to the stream. </summary>
        void Write(Guid id);

        /// <summary>
        /// Try to write a simple type (non-array) value to the stream.
        /// </summary>
        /// <param name="obj">Input object to be written to the output stream.</param>
        /// <returns>Returns <c>true</c> if the value was successfully written to the output stream.</returns>
        bool TryWriteSimpleObject(object obj);
    }
}
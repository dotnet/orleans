using System;
using System.Net;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    public interface IBinaryTokenStreamReader
    {
        /// <summary>Current read position in the stream.</summary>
        int CurrentPosition { get; }

        /// <summary>
        /// Creates a copy of the current stream reader.
        /// </summary>
        /// <returns>The new copy</returns>
        IBinaryTokenStreamReader Copy();

        /// <summary> Read a <c>bool</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        bool ReadBoolean();

        /// <summary> Read an <c>Int32</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        int ReadInt();

        /// <summary> Read an <c>UInt32</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        uint ReadUInt();

        /// <summary> Read an <c>Int16</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        short ReadShort();

        /// <summary> Read an <c>UInt16</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        ushort ReadUShort();

        /// <summary> Read an <c>Int64</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        long ReadLong();

        /// <summary> Read an <c>UInt64</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        ulong ReadULong();

        /// <summary> Read an <c>float</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        float ReadFloat();

        /// <summary> Read an <c>double</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        double ReadDouble();

        /// <summary> Read an <c>decimal</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        decimal ReadDecimal();

        DateTime ReadDateTime();

        /// <summary> Read an <c>string</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        string ReadString();

        /// <summary> Read the next bytes from the stream. </summary>
        /// <param name="count">Number of bytes to read.</param>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        byte[] ReadBytes(int count);

        /// <summary> Read the next bytes from the stream. </summary>
        /// <param name="destination">Output array to store the returned data in.</param>
        /// <param name="offset">Offset into the destination array to write to.</param>
        /// <param name="count">Number of bytes to read.</param>
        void ReadByteArray(byte[] destination, int offset, int count);

        /// <summary> Read an <c>char</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        char ReadChar();

        /// <summary> Read an <c>byte</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        byte ReadByte();

        /// <summary> Read an <c>sbyte</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        sbyte ReadSByte();

        Guid ReadGuid();

        /// <summary> Read an <c>IPAddress</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        IPAddress ReadIPAddress();

        /// <summary> Read an <c>IPEndPoint</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        IPEndPoint ReadIPEndPoint();

        /// <summary> Read an <c>SiloAddress</c> value from the stream. </summary>
        /// <returns>Data from current position in stream, converted to the appropriate output type.</returns>
        SiloAddress ReadSiloAddress();

        TimeSpan ReadTimeSpan();

        /// <summary>
        /// Read a block of data into the specified output <c>Array</c>.
        /// </summary>
        /// <param name="array">Array to output the data to.</param>
        /// <param name="n">Number of bytes to read.</param>
        void ReadBlockInto(Array array, int n);

        byte PeekByte();
    }
}
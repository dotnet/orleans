using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Buffers
{
    public ref partial struct Writer<TBufferWriter>
    {
        /// <summary>
        /// Writes a variable-width <see cref="sbyte"/>.
        /// </summary>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVarInt8(sbyte value) => WriteVarUInt28(ZigZagEncode(value));

        /// <summary>
        /// Writes a variable-width <see cref="short"/>.
        /// </summary>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVarInt16(short value) => WriteVarUInt28(ZigZagEncode(value));

        /// <summary>
        /// Writes a variable-width <see cref="int"/>.
        /// </summary>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVarInt32(int value) => WriteVarUInt32(ZigZagEncode(value));

        /// <summary>
        /// Writes a variable-width <see cref="long"/>.
        /// </summary>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVarInt64(long value) => WriteVarUInt64(ZigZagEncode(value));

        /// <summary>
        /// Writes a variable-width <see cref="byte"/>.
        /// </summary>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVarUInt8(byte value) => WriteVarUInt28(value);

        /// <summary>
        /// Writes a variable-width <see cref="ushort"/>.
        /// </summary>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVarUInt16(ushort value) => WriteVarUInt28(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint ZigZagEncode(int value) => (uint)((value << 1) ^ (value >> 31));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong ZigZagEncode(long value) => (ulong)((value << 1) ^ (value >> 63));
    }
}

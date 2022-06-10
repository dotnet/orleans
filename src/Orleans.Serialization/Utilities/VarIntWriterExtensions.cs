using System.Buffers;
#if NETCOREAPP3_1_OR_GREATER
using System.Numerics;
#endif
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.Serialization.Buffers;

namespace Orleans.Serialization.Buffers
{
    /// <summary>
    /// Extension methods for writing variable-width integers.
    /// </summary>
    public static class VarIntWriterExtensions
    {
        /// <summary>
        /// Writes a variable-width <see cref="sbyte"/>.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteVarInt8<TBufferWriter>(ref this Writer<TBufferWriter> writer, sbyte value) where TBufferWriter : IBufferWriter<byte> => WriteVarUInt8(ref writer, ZigZagEncode(value));

        /// <summary>
        /// Writes a variable-width <see cref="short"/>.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteVarInt16<TBufferWriter>(ref this Writer<TBufferWriter> writer, short value) where TBufferWriter : IBufferWriter<byte> => WriteVarUInt16(ref writer, ZigZagEncode(value));

        /// <summary>
        /// Writes a variable-width <see cref="int"/>.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteVarInt32<TBufferWriter>(ref this Writer<TBufferWriter> writer, int value) where TBufferWriter : IBufferWriter<byte> => writer.WriteVarUInt32(ZigZagEncode(value));

        /// <summary>
        /// Writes a variable-width <see cref="long"/>.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteVarInt64<TBufferWriter>(ref this Writer<TBufferWriter> writer, long value) where TBufferWriter : IBufferWriter<byte> => writer.WriteVarUInt64(ZigZagEncode(value));

        /// <summary>
        /// Writes a variable-width <see cref="byte"/>.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteVarUInt8<TBufferWriter>(ref this Writer<TBufferWriter> writer, byte value) where TBufferWriter : IBufferWriter<byte>
        {
            writer.EnsureContiguous(sizeof(ushort));

            var span = writer.WritableSpan;
            var neededBytes = BitOperations.Log2(value) / 7;

            ushort lower = value;
            lower <<= 1;
            lower |= 0x01;
            lower <<= neededBytes;

            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(span), lower);
            writer.AdvanceSpan(neededBytes + 1);
        }

        /// <summary>
        /// Writes a variable-width <see cref="ushort"/>.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteVarUInt16<TBufferWriter>(ref this Writer<TBufferWriter> writer, ushort value) where TBufferWriter : IBufferWriter<byte>
        {
            writer.EnsureContiguous(sizeof(uint));

            var span = writer.WritableSpan;
            var neededBytes = BitOperations.Log2(value) / 7;

            uint lower = value;
            lower <<= 1;
            lower |= 0x01;
            lower <<= neededBytes;

            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(span), lower);
            writer.AdvanceSpan(neededBytes + 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte ZigZagEncode(sbyte value) => (byte)((value << 1) ^ (value >> 7));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort ZigZagEncode(short value) => (ushort)((value << 1) ^ (value >> 15));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ZigZagEncode(int value) => (uint)((value << 1) ^ (value >> 31));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong ZigZagEncode(long value) => (ulong)((value << 1) ^ (value >> 63));
    }
}

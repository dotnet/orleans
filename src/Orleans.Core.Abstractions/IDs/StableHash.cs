using System;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

#nullable enable
namespace Orleans
{
    public static class StableHash
    {
        /// <summary>
        /// Computes a hash digest of the input.
        /// </summary>
        /// <param name="data">
        /// The input data.
        /// </param>
        /// <returns>
        /// A hash digest of the input.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe uint ComputeHash(ReadOnlySpan<byte> data)
        {
            uint hash;
            XxHash32.TryHash(data, new Span<byte>((byte*)&hash, sizeof(uint)), out _);
            return BitConverter.IsLittleEndian ? hash : BinaryPrimitives.ReverseEndianness(hash);
        }

        /// <summary>
        /// Computes a hash digest of the input.
        /// </summary>
        /// <param name="data">
        /// The input data.
        /// </param>
        /// <returns>
        /// A hash digest of the input.
        /// </returns>
        public static uint ComputeHash(string data) => ComputeHash(BitConverter.IsLittleEndian ? MemoryMarshal.AsBytes(data.AsSpan()) : Encoding.Unicode.GetBytes(data));
    }
}

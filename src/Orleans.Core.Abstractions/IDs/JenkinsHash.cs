using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

#nullable enable
namespace Orleans
{
    /// <summary>
    /// Implements Bob Jenkins' hashing algorithm.
    /// </summary>
    /// <seealso href="https://en.wikipedia.org/wiki/Jenkins_hash_function"/>
    public static class JenkinsHash
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Mix(ref uint a, ref uint b, ref uint c)
        {
            a -= b; a -= c; a ^= (c >> 13);
            b -= c; b -= a; b ^= (a << 8);
            c -= a; c -= b; c ^= (b >> 13);
            a -= b; a -= c; a ^= (c >> 12);
            b -= c; b -= a; b ^= (a << 16);
            c -= a; c -= b; c ^= (b >> 5);
            a -= b; a -= c; a ^= (c >> 3);
            b -= c; b -= a; b ^= (a << 10);
            c -= a; c -= b; c ^= (b >> 15);
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
        public static uint ComputeHash(ReadOnlySpan<byte> data)
        {
            int len = data.Length;
            uint a = 0x9e3779b9;
            uint b = a;
            uint c = 0;
            ref var buf = ref MemoryMarshal.GetReference(data);

            var remaining = len;
            for (; remaining >= 12; remaining -= 12)
            {
                a += ReadUInt32(ref buf);
                b += ReadUInt32(ref Unsafe.AddByteOffset(ref buf, 4));
                c += ReadUInt32(ref Unsafe.AddByteOffset(ref buf, 8));
                Mix(ref a, ref b, ref c);
                buf = ref Unsafe.AddByteOffset(ref buf, 12);
            }
            c += (uint)len;

            switch (remaining)
            {
                case 11: c += (uint)Unsafe.AddByteOffset(ref buf, 10) << 24; goto case 10;
                case 10: c += (uint)Unsafe.AddByteOffset(ref buf, 9) << 16; goto case 9;
                case 9: c += (uint)Unsafe.AddByteOffset(ref buf, 8) << 8; goto case 8;
                case 8: b += ReadUInt32(ref Unsafe.AddByteOffset(ref buf, 4)); goto case 4;
                case 7: b += (uint)Unsafe.AddByteOffset(ref buf, 6) << 16; goto case 6;
                case 6: b += (uint)Unsafe.AddByteOffset(ref buf, 5) << 8; goto case 5;
                case 5: b += (uint)Unsafe.AddByteOffset(ref buf, 4); goto case 4;
                case 4: a += ReadUInt32(ref buf); break;
                case 3: a += (uint)Unsafe.AddByteOffset(ref buf, 2) << 16; goto case 2;
                case 2: a += (uint)Unsafe.AddByteOffset(ref buf, 1) << 8; goto case 1;
                case 1: a += buf; break;
            }

            Mix(ref a, ref b, ref c);
            return c;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static uint ReadUInt32(ref byte buf) => BitConverter.IsLittleEndian ? Unsafe.ReadUnaligned<uint>(ref buf) : BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<uint>(ref buf));
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

        /// <summary>
        /// Computes a hash digest of the input.
        /// </summary>
        /// <param name="u1">
        /// The first input.
        /// </param>
        /// <param name="u2">
        /// The second input.
        /// </param>
        /// <param name="u3">
        /// The third input.
        /// </param>
        /// <returns>
        /// A hash digest of the input.
        /// </returns>
        public static uint ComputeHash(ulong u1, ulong u2, ulong u3)
        {
            // This implementation calculates the exact same hash value as the above, but is
            // optimized for the case where the input is exactly 24 bytes of data provided as
            // three 8-byte unsigned integers.
            uint a = 0x9e3779b9;
            uint b = a;
            uint c = 0;

            unchecked
            {
                a += (uint)u1;
                b += (uint)(u1 >> 32);
                c += (uint)u2;
                Mix(ref a, ref b, ref c);
                a += (uint)(u2 >> 32);
                b += (uint)u3;
                c += (uint)(u3 >> 32);
            }
            Mix(ref a, ref b, ref c);
            c += 24;
            Mix(ref a, ref b, ref c);
            return c;
        }
    }
}

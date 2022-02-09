using System;
using System.Text;

namespace Orleans
{
    /// <summary>
    /// Implements Bob Jenkins' hashing algorithm.
    /// </summary>
    /// <seealso href="https://en.wikipedia.org/wiki/Jenkins_hash_function"/>
    public static class JenkinsHash
    {
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
        public static uint ComputeHash(byte[] data)
        {
            return ComputeHash(data.AsSpan());
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
            int i = 0;

            while (i + 12 <= len)
            {
                a += (uint)data[i++] |
                    ((uint)data[i++] << 8) |
                    ((uint)data[i++] << 16) |
                    ((uint)data[i++] << 24);
                b += (uint)data[i++] |
                    ((uint)data[i++] << 8) |
                    ((uint)data[i++] << 16) |
                    ((uint)data[i++] << 24);
                c += (uint)data[i++] |
                    ((uint)data[i++] << 8) |
                    ((uint)data[i++] << 16) |
                    ((uint)data[i++] << 24);
                Mix(ref a, ref b, ref c);
            }
            c += (uint)len;
            if (i < len)
                a += data[i++];
            if (i < len)
                a += (uint)data[i++] << 8;
            if (i < len)
                a += (uint)data[i++] << 16;
            if (i < len)
                a += (uint)data[i++] << 24;
            if (i < len)
                b += (uint)data[i++];
            if (i < len)
                b += (uint)data[i++] << 8;
            if (i < len)
                b += (uint)data[i++] << 16;
            if (i < len)
                b += (uint)data[i++] << 24;
            if (i < len)
                c += (uint)data[i++] << 8;
            if (i < len)
                c += (uint)data[i++] << 16;
            if (i < len)
                c += (uint)data[i++] << 24;
            Mix(ref a, ref b, ref c);
            return c;
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
        public static uint ComputeHash(string data)
        {
            byte[] bytesToHash = Encoding.UTF8.GetBytes(data);
            return ComputeHash(bytesToHash);
        }

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
                b += (uint)((u1 ^ (uint)u1) >> 32);
                c += (uint)u2;
                Mix(ref a, ref b, ref c);
                a += (uint)((u2 ^ (uint)u2) >> 32);
                b += (uint)u3;
                c += (uint)((u3 ^ (uint)u3) >> 32);
            }
            Mix(ref a, ref b, ref c);
            c += 24;
            Mix(ref a, ref b, ref c);
            return c;
        }
    }
}

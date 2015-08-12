/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System.Text;
using System.Threading;

namespace Orleans
{
    // Based on the version in http://home.comcast.net/~bretm/hash/7.html, which is based on that
    // in http://burtleburtle.net/bob/hash/evahash.html.
    // Note that we only use the version that takes three ulongs, which was written by the Orleans team.
    internal class JenkinsHash
    {
        internal static class Factory
        {
            private static readonly ThreadLocal<JenkinsHash> hashGenerator = new ThreadLocal<JenkinsHash>(() => new JenkinsHash());

            /// <summary>
            /// Get an instance of Jenkins hash generator
            /// </summary>
            /// <param name="threadLocal">Whether instance should be ThreadLocal to provide thread safe instance</param>
            /// <returns>Hash generator instance</returns>
            public static JenkinsHash GetHashGenerator(bool threadLocal = true)
            {
                return (threadLocal) ? hashGenerator.Value : new JenkinsHash();
            }
        }

        // Private constructor so that we control instance creation
        private JenkinsHash() {}

        uint a, b, c;

        private void Mix()
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

        // This is the reference implementation of the Jenkins hash.
        public uint ComputeHash(byte[] data)
        {
            int len = data.Length;
            a = b = 0x9e3779b9;
            c = 0;
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
                Mix();
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
            Mix();
            return c;
        }

        public uint ComputeHash(string data)
        {
            byte[] bytesToHash = Encoding.UTF8.GetBytes(data);
            return ComputeHash(bytesToHash);
        }

        // This implementation calculates the exact same hash value as the above, but is
        // optimized for the case where the input is exactly 24 bytes of data provided as
        // three 8-byte unsigned integers.
        public uint ComputeHash(ulong u1, ulong u2, ulong u3)
        {
            a = b = 0x9e3779b9;
            c = 0;
            unchecked
            {
                a += (uint)u1;
                b += (uint)((u1 ^ (uint)u1) >> 32);
                c += (uint)u2;
                Mix();
                a += (uint)((u2 ^ (uint)u2) >> 32);
                b += (uint)u3;
                c += (uint)((u3 ^ (uint)u3) >> 32);
            }
            Mix();
            c += 24;
            Mix();
            return c;
        } 
    }
}
using System;
using System.Buffers;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    public static class ArrayPoolExtensions
    {

        public static byte[] GetBuffer(this ArrayPool<byte> pool)
        {
            return pool.Rent(0);
        }


        public static void Release(this ArrayPool<byte> pool, byte[] buffer)
        {
            pool.Return(buffer);
        }

        public static List<ArraySegment<byte>> GetMultiBuffer(this ArrayPool<byte> pool, int totalSize)
        {
            var list = new List<ArraySegment<byte>>();
            while (totalSize > 0)
            {
                var buff = pool.Rent(0);
                var byteBufferSize = buff.Length;
                list.Add(new ArraySegment<byte>(buff, 0, Math.Min(byteBufferSize, totalSize)));
                totalSize -= byteBufferSize;
            }
            return list;
        }

        public static void Release(this ArrayPool<byte> pool, List<ArraySegment<byte>> list)
        {
            if (list == null) return;

            foreach (var segment in list)
            {
                pool.Return(segment.Array);
            }
        }
    }
}
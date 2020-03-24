using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Persistence.S3
{
    internal static class StreamExtensions
    {
        public static ArraySegment<byte> AsArraySegment(this MemoryStream mem)
            => mem.TryGetBuffer(out var buffer)
                ? buffer
                : new ArraySegment<byte>(mem.ToArray());

        public static async Task<ArraySegment<byte>> AsArraySegmentAsync(this Stream stream, int initialCapacity, CancellationToken cancellationToken = default)
        {
            if (!(stream is MemoryStream mem))
            {
                mem = initialCapacity == 0 ? new MemoryStream() : new MemoryStream(initialCapacity);
                await stream
                    .CopyToAsync(mem, 8192, cancellationToken)
                    .ConfigureAwait(false);
            }

            return mem.AsArraySegment();
        }

        public static ArraySegment<byte> MergeToSingleSegment(this IReadOnlyList<ArraySegment<byte>> segments)
        {
            switch( segments.Count ) {
                case 0: return default(ArraySegment<byte>);
                case 1: return segments[0];
            }

            var size = segments.Sum(x => x.Count);
            var buffer = new byte[size];
            var offset = 0;
            foreach (var segment in segments)
            {
                Buffer.BlockCopy(segment.Array, segment.Offset, buffer, offset, segment.Count);
                offset += segment.Count;
            }

            return new ArraySegment<byte>(buffer);
        }

        public static MemoryStream AsMemoryStream(this ArraySegment<byte> bytes, bool writable = false) => new MemoryStream(bytes.Array, bytes.Offset, bytes.Count, writable, true);
    }
}
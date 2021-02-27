using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Orleans.Serialization.TestKit
{
    [ExcludeFromCodeCoverage]
    public static class ReadOnlySequenceHelper
    {
        public static IEnumerable<byte[]> Batch(this IEnumerable<byte> sequence, int batchSize)
        {
            var batch = new List<byte>(batchSize);
            foreach (var item in sequence)
            {
                batch.Add(item);

                if (batch.Count >= batchSize)
                {
                    yield return batch.ToArray();
                    batch = new List<byte>(batchSize);
                }
            }

            if (batch.Count > 0)
            {
                yield return batch.ToArray();
            }
        }

        public static ReadOnlySequence<byte> ToReadOnlySequence(this IEnumerable<byte[]> buffers) => CreateReadOnlySequence(buffers.ToArray());

        public static ReadOnlySequence<byte> ToReadOnlySequence(this IEnumerable<Memory<byte>> buffers) => ReadOnlyBufferSegment.Create(buffers);

        public static ReadOnlySequence<byte> CreateReadOnlySequence(params byte[][] buffers)
        {
            if (buffers.Length == 1)
            {
                return new ReadOnlySequence<byte>(buffers[0]);
            }

            var list = new List<Memory<byte>>();
            foreach (var buffer in buffers)
            {
                list.Add(buffer);
            }

            return ToReadOnlySequence(list.ToArray());
        }

        private class ReadOnlyBufferSegment : ReadOnlySequenceSegment<byte>
        {
            public static ReadOnlySequence<byte> Create(IEnumerable<Memory<byte>> buffers)
            {
                ReadOnlyBufferSegment segment = null;
                ReadOnlyBufferSegment first = null;
                foreach (var buffer in buffers)
                {
                    var newSegment = new ReadOnlyBufferSegment
                    {
                        Memory = buffer,
                    };

                    if (segment != null)
                    {
                        segment.Next = newSegment;
                        newSegment.RunningIndex = segment.RunningIndex + segment.Memory.Length;
                    }
                    else
                    {
                        first = newSegment;
                    }

                    segment = newSegment;
                }

                if (first is null)
                {
                    first = segment = new ReadOnlyBufferSegment();
                }

                return new ReadOnlySequence<byte>(first, 0, segment, segment.Memory.Length);
            }
        }
    }
}
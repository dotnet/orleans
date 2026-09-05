using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Orleans.Serialization.TestKit
{
    /// <summary>
    /// Provides helpers for creating segmented <see cref="ReadOnlySequence{T}"/> instances.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public static class ReadOnlySequenceHelper
    {
        /// <summary>
        /// Groups a sequence of bytes into arrays containing at most <paramref name="batchSize"/> bytes.
        /// </summary>
        /// <param name="sequence">The sequence to divide into batches.</param>
        /// <param name="batchSize">The maximum number of bytes in each batch.</param>
        /// <returns>A sequence of byte arrays containing the source bytes in order.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="sequence"/> is <see langword="null"/>.</exception>
        public static IEnumerable<byte[]> Batch(this IEnumerable<byte> sequence, int batchSize)
        {
            if (sequence is null)
            {
                throw new ArgumentNullException(nameof(sequence));
            }

            return BatchCore(sequence, batchSize);

            static IEnumerable<byte[]> BatchCore(IEnumerable<byte> sequence, int batchSize)
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
        }

        /// <summary>
        /// Creates a read-only sequence whose segments are backed by the provided byte arrays.
        /// </summary>
        /// <param name="buffers">The buffers to include as sequence segments.</param>
        /// <returns>A read-only sequence containing the provided buffers in order.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="buffers"/> is <see langword="null"/>.</exception>
        public static ReadOnlySequence<byte> ToReadOnlySequence(this IEnumerable<byte[]> buffers)
        {
            if (buffers is null)
            {
                throw new ArgumentNullException(nameof(buffers));
            }

            return CreateReadOnlySequence(buffers.ToArray());
        }

        /// <summary>
        /// Creates a read-only sequence whose segments are backed by the provided memory regions.
        /// </summary>
        /// <param name="buffers">The memory regions to include as sequence segments.</param>
        /// <returns>A read-only sequence containing the provided memory regions in order.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="buffers"/> is <see langword="null"/>.</exception>
        public static ReadOnlySequence<byte> ToReadOnlySequence(this IEnumerable<Memory<byte>> buffers)
        {
            if (buffers is null)
            {
                throw new ArgumentNullException(nameof(buffers));
            }

            return ReadOnlyBufferSegment.Create(buffers);
        }

        /// <summary>
        /// Creates a read-only sequence whose segments are backed by the provided byte arrays.
        /// </summary>
        /// <param name="buffers">The buffers to include as sequence segments.</param>
        /// <returns>A read-only sequence containing the provided buffers in order.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="buffers"/> is <see langword="null"/>.</exception>
        public static ReadOnlySequence<byte> CreateReadOnlySequence(params byte[][] buffers)
        {
            if (buffers is null)
            {
                throw new ArgumentNullException(nameof(buffers));
            }

            if (buffers.Length == 1)
            {
                return new ReadOnlySequence<byte>(buffers[0]);
            }

            var list = new List<Memory<byte>>();
            foreach (var buffer in buffers)
            {
                list.Add(buffer);
            }

            return ToReadOnlySequence(list);
        }

        private class ReadOnlyBufferSegment : ReadOnlySequenceSegment<byte>
        {
            public static ReadOnlySequence<byte> Create(IEnumerable<Memory<byte>> buffers)
            {
                ReadOnlyBufferSegment? segment = null;
                ReadOnlyBufferSegment? first = null;
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

                return new ReadOnlySequence<byte>(first, 0, segment!, segment!.Memory.Length);
            }
        }
    }
}

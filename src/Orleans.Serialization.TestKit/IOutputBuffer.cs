using System.Buffers;

namespace Orleans.Serialization.TestKit
{
    /// <summary>
    /// Provides access to the bytes written to an output buffer.
    /// </summary>
    public interface IOutputBuffer
    {
        /// <summary>
        /// Returns the written bytes as a read-only sequence.
        /// </summary>
        /// <param name="maxSegmentSize">The maximum number of bytes in each sequence segment.</param>
        /// <returns>A read-only sequence containing the written bytes.</returns>
        ReadOnlySequence<byte> GetReadOnlySequence(int maxSegmentSize);
    }
}
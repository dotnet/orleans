using System.Buffers;

namespace Orleans.Serialization.TestKit
{
    public interface IOutputBuffer
    {
        ReadOnlySequence<byte> GetReadOnlySequence(int maxSegmentSize);
    }
}
using System.Buffers;

namespace Orleans.Networking.Shared
{
    internal static class KestrelMemoryPool
    {
        public static MemoryPool<byte> Create() => CreateSlabMemoryPool();

        public static MemoryPool<byte> CreateSlabMemoryPool() => new SlabMemoryPool();

        public static readonly int MinimumSegmentSize = 4096;
    }
}

using System.Buffers;

namespace Orleans.Networking.Shared
{
    internal static class SharedMemoryPool
    {
        public static MemoryPool<byte> Pool { get; } = PinnedBlockMemoryPoolFactory.Create();
    }
}

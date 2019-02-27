using System.Buffers;

namespace Orleans.Networking.Shared
{
    internal sealed class SharedMemoryPool
    {
        public MemoryPool<byte> Pool { get; } = KestrelMemoryPool.Create();
    }
}

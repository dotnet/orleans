using Microsoft.Extensions.ObjectPool;
using Orleans.Serialization.Buffers;

namespace Orleans.DurableMessaging;

internal static class DurableMessageBuffers
{
    internal static readonly ObjectPool<ArcBufferWriter> Pool = ObjectPool.Create<ArcBufferWriter>();
}

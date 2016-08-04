using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Providers.Streams.Memory
{
    public interface IMemoryStreamQueueGrain : IGrainWithGuidKey
    {
        Task SetMaxEventCount(int maxEventCount);

        Task Enqueue(MemoryEventData eventData);
         
        Task<List<MemoryEventData>> Dequeue(int maxCount);
    }
}

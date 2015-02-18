using System.Collections.Generic;
using System.Threading.Tasks;

namespace OrleansProviders.PersistentStream.MockQueueAdapter
{
    public interface IMockQueueAdapterBatchGenerator
    {
        Task<IEnumerable<MockQueueAdapterBatchContainer>> GetQueueMessagesAsync(int targetBatchesPerSecond);
    }
}
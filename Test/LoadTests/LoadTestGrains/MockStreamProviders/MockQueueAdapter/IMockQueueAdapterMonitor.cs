using System;
using Orleans.Streams;

namespace OrleansProviders.PersistentStream.MockQueueAdapter
{
    public interface IMockQueueAdapterMonitor
    {
        void AdapterCreated();
        void BatchDeliveredToConsumer(Guid streamGuid, string streamNamespace);
        void ReceiverCreated(QueueId queue);
        void AddToCache(int count);
        void NewCursor(Guid streamGuid, string streamNamespace);
        void LowBackPressure(QueueId queue, int batchesPerSecond, double backPressure);
        void HighBackPressure(QueueId queue, int batchesPerSecond, double backPressure);
    }
}
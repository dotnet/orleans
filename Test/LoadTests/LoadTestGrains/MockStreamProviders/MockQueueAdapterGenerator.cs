using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams;
using OrleansProviders.PersistentStream.MockQueueAdapter;

namespace LoadTestGrains.MockStreamProviders
{
    /// <summary>
    /// First pass generater
    /// Uses IStreamNamespaceMessageProducer for now
    /// TODO: Refactor or replace IStreamNamespaceMessageProducer
    /// </summary>
    internal class MockQueueAdapterGenerator : IMockQueueAdapterBatchGenerator
    {
        private readonly IStreamNamespaceMessageProducer _provider;

        public MockQueueAdapterGenerator(MockStreamProviderSettings settings, Logger logger)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }
            if (logger == null)
            {
                throw new ArgumentNullException("logger");
            }
            switch (settings.MessageProducer.ToLowerInvariant())
            {
                case "implicitconsumer": _provider = new ImplicitConsumerMessageProducer(settings, logger); break;
                default: throw new InvalidOperationException(string.Format("Invalid MessageProducer \"{0}\"", settings.MessageProducer));
            }
        }

        public async Task<IEnumerable<MockQueueAdapterBatchContainer>> GetQueueMessagesAsync(int targetBatchesPerSecond)
        {
            await Task.Delay(20);
            return (await _provider.GetQueueMessagesAsync(targetBatchesPerSecond)).Select(BatchContainerWrapper.Create);
        }

        [Serializable]
        private class BatchContainerWrapper : MockQueueAdapterBatchContainer
        {
            private readonly IBatchContainer _batchContainer;

            public static MockQueueAdapterBatchContainer Create(IBatchContainer batchContainer)
            {
                if (batchContainer == null)
                {
                    throw new ArgumentNullException("batchContainer");
                }
                return new BatchContainerWrapper(batchContainer);
            }

            private BatchContainerWrapper(IBatchContainer batchContainer)
                : base(batchContainer.StreamGuid, batchContainer.StreamNamespace)
            {
                _batchContainer = batchContainer;
            }

            protected override IEnumerable<T> GetEventsInternal<T>()
            {
                return _batchContainer.GetEvents<T>().Select(tuple => tuple.Item1);
            }

            public override bool ShouldDeliver(IStreamIdentity stream, object filterData, StreamFilterPredicate shouldReceiveFunc)
            {
                return _batchContainer.ShouldDeliver(stream, filterData, shouldReceiveFunc);
            }
        }
    }
}
using System;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.RabbitMQ.Providers
{
    public class RabbitMQQueueCacheFactory : IRabbitMQQueueCacheFactory
    {
        private readonly StreamStatisticOptions _statisticOptions;
        private readonly SerializationManager _serializationManager;
        private readonly StreamCacheEvictionOptions _streamCacheEvictionOptions;
        private string _bufferPoolId;
        private IObjectPool<FixedSizeBuffer> _bufferPool;

        public RabbitMQQueueCacheFactory(StreamStatisticOptions statisticOptions, SerializationManager serializationManager, StreamCacheEvictionOptions streamCacheEvictionOptions)
        {
            this._statisticOptions = statisticOptions ?? throw new ArgumentNullException(nameof(statisticOptions));
            this._serializationManager = serializationManager ?? throw new ArgumentNullException(nameof(serializationManager));
            this._streamCacheEvictionOptions = streamCacheEvictionOptions ?? throw new ArgumentNullException(nameof(streamCacheEvictionOptions));
        }



        // undone (mxplusb): finish the cache creation policy.
        public IRabbitMQQueueCache CreateCache(string partition, IStreamQueueCheckpointer<string> checkpointer, ILoggerFactory loggerFactory, ITelemetryProducer telemetryProducer)
        {
            string blockPoolId;
            var blockPool = CreateBufferPool(loggerFactory, out blockPoolId);
        }

        protected virtual IRabbitMQQueueCache CreateCache(string partition,
                                                          IStreamQueueCheckpointer<string> checkpointer,
                                                          ILoggerFactory loggerFactory,
                                                          IObjectPool<FixedSizeBuffer> objectPool,
                                                          string blockPoolId,
                                                          TimePurgePredicate timePurge,
                                                          SerializationManager serializationManager,
                                                          ITelemetryProducer telemetryProducer)
        {

        }

        protected virtual IObjectPool<FixedSizeBuffer> CreateBufferPool(ILoggerFactory loggerFactory, out string blockPoolId)
        {
            if (_bufferPool == null)
            {
                var bufferSize = 1 << 20;
                _bufferPoolId = $"BlockPool-{new Guid().ToString()}-BlockSize-{bufferSize}";
                _bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(bufferSize));
            }
            blockPoolId = _bufferPoolId;
            return _bufferPool;
        }
    }
}

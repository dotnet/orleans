using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Providers.GCP.Streams.PubSub
{
    internal class PubSubAdapter<TDataAdapter> : IQueueAdapter
        where TDataAdapter : IPubSubDataAdapter
    {
        protected readonly string ServiceId;
        protected readonly string ProjectId;
        protected readonly string TopicId;
        protected readonly TimeSpan? Deadline;
        private readonly HashRingBasedStreamQueueMapper _streamQueueMapper;
        protected readonly ConcurrentDictionary<QueueId, PubSubDataManager> Subscriptions = new ConcurrentDictionary<QueueId, PubSubDataManager>();
        protected readonly IPubSubDataAdapter _dataAdapter;
        private readonly ILogger _logger;
        private readonly ILoggerFactory loggerFactory;
        private readonly string _customEndpoint;
        public string Name { get; }
        public bool IsRewindable => false;
        public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

        public PubSubAdapter(
            TDataAdapter dataAdapter,
            ILoggerFactory loggerFactory,
            HashRingBasedStreamQueueMapper streamQueueMapper,
            string projectId,
            string topicId,
            string serviceId,
            string providerName,
            TimeSpan? deadline = null, 
            string customEndpoint = "")
        {
            if (string.IsNullOrEmpty(projectId)) throw new ArgumentNullException(nameof(projectId));
            if (string.IsNullOrEmpty(topicId)) throw new ArgumentNullException(nameof(topicId));
            if (string.IsNullOrEmpty(serviceId)) throw new ArgumentNullException(nameof(serviceId));

            _logger = loggerFactory.CreateLogger($"{this.GetType().FullName}.{providerName}");
            this.loggerFactory = loggerFactory;
            ProjectId = projectId;
            TopicId = topicId;
            ServiceId = serviceId;
            Name = providerName;
            Deadline = deadline;
            _streamQueueMapper = streamQueueMapper;
            _dataAdapter = dataAdapter;
            _customEndpoint = customEndpoint;
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return PubSubAdapterReceiver.Create(this.loggerFactory, queueId, ProjectId, TopicId, ServiceId, _dataAdapter, Deadline, _customEndpoint);
        }

        public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            if (token != null) throw new ArgumentException("Google PubSub stream provider currently does not support non-null StreamSequenceToken.", nameof(token));
            var queueId = _streamQueueMapper.GetQueueForStream(streamId);

            PubSubDataManager pubSub;
            if (!Subscriptions.TryGetValue(queueId, out pubSub))
            {
                var tmpPubSub = new PubSubDataManager(this.loggerFactory, ProjectId, TopicId, queueId.ToString(), ServiceId, Deadline);
                await tmpPubSub.Initialize();
                pubSub = Subscriptions.GetOrAdd(queueId, tmpPubSub);
            }

            var msg = _dataAdapter.ToPubSubMessage(streamId, events, requestContext);
            await pubSub.PublishMessages(new[] { msg });
        }
    }
}

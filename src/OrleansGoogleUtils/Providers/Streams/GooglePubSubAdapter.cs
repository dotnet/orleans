using Orleans.Streams;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Serialization.Providers.Streams
{
    internal class GooglePubSubAdapter<TDataAdapter> : IQueueAdapter
        where TDataAdapter : IGooglePubSubDataAdapter
    {
        protected readonly string DeploymentId;
        private readonly SerializationManager _serializationManager;
        protected readonly string ProjectId;
        protected readonly string TopicId;
        protected readonly TimeSpan? Deadline;
        private readonly HashRingBasedStreamQueueMapper _streamQueueMapper;
        protected readonly ConcurrentDictionary<QueueId, GooglePubSubDataManager> Subscriptions = new ConcurrentDictionary<QueueId, GooglePubSubDataManager>();
        protected readonly IGooglePubSubDataAdapter _dataAdapter;

        public string Name { get; }
        public bool IsRewindable => false;
        public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

        public GooglePubSubAdapter(
            TDataAdapter dataAdapter,
            SerializationManager serializationManager,
            HashRingBasedStreamQueueMapper streamQueueMapper,
            string projectId,
            string topicId,
            string deploymentId,
            string providerName,
            TimeSpan? deadline = null)
        {
            if (string.IsNullOrEmpty(projectId)) throw new ArgumentNullException(nameof(projectId));
            if (string.IsNullOrEmpty(topicId)) throw new ArgumentNullException(nameof(topicId));
            if (string.IsNullOrEmpty(deploymentId)) throw new ArgumentNullException(nameof(deploymentId));

            _serializationManager = serializationManager;
            ProjectId = projectId;
            TopicId = topicId;
            DeploymentId = deploymentId;
            Name = providerName;
            Deadline = deadline;
            _streamQueueMapper = streamQueueMapper;
            _dataAdapter = dataAdapter;
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return GooglePubSubAdapterReceiver.Create(_serializationManager, queueId, ProjectId, TopicId, DeploymentId, _dataAdapter, Deadline);
        }

        public async Task QueueMessageBatchAsync<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            if (token != null) throw new ArgumentException("Google PubSub stream provider currently does not support non-null StreamSequenceToken.", nameof(token));
            var queueId = _streamQueueMapper.GetQueueForStream(streamGuid, streamNamespace);

            GooglePubSubDataManager pubSub;
            if (!Subscriptions.TryGetValue(queueId, out pubSub))
            {
                var tmpPubSub = new GooglePubSubDataManager(ProjectId, TopicId, queueId.ToString(), DeploymentId, Deadline);
                await tmpPubSub.Initialize();
                pubSub = Subscriptions.GetOrAdd(queueId, tmpPubSub);
            }

            var msg = _dataAdapter.ToPubSubMessage(streamGuid, streamNamespace, events, requestContext);
            await pubSub.PublishMessages(new[] { msg });
        }
    }
}

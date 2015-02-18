using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LoadTestGrainInterfaces;
using LoadTestGrains.MockStreamProviders;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using OrleansProviders.PersistentStream.MockQueueAdapter;

namespace LoadTestGrains
{
    public class MockStreamProvider : PersistentStreamProvider<MockQueueAdapterFactory> { }

    public class MockStreamProviderSettings : IMockQueueAdapterSettings
    {
        private const string STREAM_PROVIDER_NAME = "StreamProvider";
        private const string DEFAULT_STREAM_PROVIDER = "MockStreamProvider";
        private static readonly int DEFAULT_NUMPULLING_AGENTS = Math.Max(1, Environment.ProcessorCount / 2); 
        private const string TOTAL_QUEUE_COUNT_NAME = "TotalQueueCount";
        private const int DEFAULT_TOTAL_QUEUE_COUNT = 128;
        private const string NUM_STREAMS_PER_QUEUE_NAME = "NumStreamsPerQueue";
        private const int DEFAULT_NUM_STREAMS_PER_QUEUE = 1;
        private const string MESSAGE_PRODUCER_NAME = "MessageProducer";
        private const string DEFAULT_MESSAGE_PRODUCER = "ImplicitConsumer"; //"ReentrantImplicitConsumer";
        private const string ADDITIONAL_SUBSCRIBERS_COUNT_NAME = "AdditionalSubscribersCount";
        private const int DEFAULT_ADDITIONAL_SUBSCRIBERS_COUNT = 0;
        private const string ACTIVATION_TASK_DELAY_NAME = "ActivationTaskDelay";
        private const int DEFAULT_ACTIVATION_TASK_DELAY = 0;
        private const string ACTIVATION_BUSY_WAIT_NAME = "ActivationBusyWait";
        private const int DEFAULT_ACTIVATION_BUSY_WAIT = 0;
        private const string EVENT_TASK_DELAY_NAME = "EventTaskDelay";
        private const int DEFAULT_EVENT_TASK_DELAY = 0;
        private const string EVENT_BUSY_WAIT_NAME = "EventBusyWait";
        private const int DEFAULT_EVENT_BUSY_WAIT = 0;
        private const string SILO_STABILIZATION_TIME = "SiloStabilizationTime";
        private const int DEFAULT_SILO_STABILIZATION_TIME = 60000;
        private const string RAMP_UP_STAGGER = "RampUpStagger";
        private const int DEFAULT_RAMP_UP_STAGGER = 6 * 1000; // 6 seconds
        private const string SUBSCRIPTION_LENGTH = "SubscriptionLength";
        private const int DEFAULT_SUBSCRIPTION_LENGTH = 60 * 1000; // 1 minute
        private const string STREAM_EVENTS_PER_SECOND = "StreamEventsPerSecond";
        private const int DEFAULT_STREAM_EVENTS_PER_SECOND = 1;
        private const string TARGET_BATCHES_SENT_PER_SECOND_NAME = "TargetBatchesSentPerSecond";
        private const int DEFAULT_TARGET_BATCHES_SENT_PER_SECOND = 100;
        private const string MAX_BATCHES_PER_REQUEST = "MaxBatchesPerRequest";
        private const int DEFAULT_MAX_BATCHES_PER_REQUEST = 1;
        private const string MAX_EVENTS_PER_BATCH = "MaxEventsPerBatch";
        private const int DEFAULT_MAX_EVENTS_PER_BATCH = 1;
        private const string EVENT_SIZE = "EventSize";
        private const int DEFAULT_EVENT_SIZE = 0;
        private const string DEPLOYMENT_ID_NAME = "DeploymentId";
        private const string DEFAULT_DEPLOYMENT_ID = "UnspecifiedMockStreamProviderDeploymentId";

        private const string CACHE_SIZE_KB_NAME = "CacheSizeKb";
        private const int DEFAULT_CACHE_SIZE_KB = 4;

        public MockStreamProviderSettings(IDictionary<string, string> properties)
        {
            StreamProvider = GetStringSetting(properties, STREAM_PROVIDER_NAME, DEFAULT_STREAM_PROVIDER);
            TotalQueueCount = GetIntSetting(properties, TOTAL_QUEUE_COUNT_NAME, DEFAULT_TOTAL_QUEUE_COUNT);
            NumStreamsPerQueue = GetIntSetting(properties, NUM_STREAMS_PER_QUEUE_NAME, DEFAULT_NUM_STREAMS_PER_QUEUE);
            MessageProducer = GetStringSetting(properties, MESSAGE_PRODUCER_NAME, DEFAULT_MESSAGE_PRODUCER);
            AdditionalSubscribersCount = GetIntSetting(properties, ADDITIONAL_SUBSCRIBERS_COUNT_NAME, DEFAULT_ADDITIONAL_SUBSCRIBERS_COUNT);
            ActivationTaskDelay = GetIntSetting(properties, ACTIVATION_TASK_DELAY_NAME, DEFAULT_ACTIVATION_TASK_DELAY);
            ActivationBusyWait = GetIntSetting(properties, ACTIVATION_BUSY_WAIT_NAME, DEFAULT_ACTIVATION_BUSY_WAIT);
            EventTaskDelay = GetIntSetting(properties, EVENT_TASK_DELAY_NAME, DEFAULT_EVENT_TASK_DELAY);
            EventBusyWait = GetIntSetting(properties, EVENT_BUSY_WAIT_NAME, DEFAULT_EVENT_BUSY_WAIT);
            SiloStabilizationTime = GetIntSetting(properties, SILO_STABILIZATION_TIME, DEFAULT_SILO_STABILIZATION_TIME);
            RampUpStagger = GetIntSetting(properties, RAMP_UP_STAGGER, DEFAULT_RAMP_UP_STAGGER);
            SubscriptionLength = GetIntSetting(properties, SUBSCRIPTION_LENGTH, DEFAULT_SUBSCRIPTION_LENGTH);
            StreamEventsPerSecond = GetIntSetting(properties, STREAM_EVENTS_PER_SECOND, DEFAULT_STREAM_EVENTS_PER_SECOND);
            TargetBatchesSentPerSecond = GetIntSetting(properties, TARGET_BATCHES_SENT_PER_SECOND_NAME, DEFAULT_TARGET_BATCHES_SENT_PER_SECOND);
            MaxBatchesPerRequest = GetIntSetting(properties, MAX_BATCHES_PER_REQUEST, DEFAULT_MAX_BATCHES_PER_REQUEST);
            MaxEventsPerBatch = GetIntSetting(properties, MAX_EVENTS_PER_BATCH, DEFAULT_MAX_EVENTS_PER_BATCH);
            EventSize = GetIntSetting(properties, EVENT_SIZE, DEFAULT_EVENT_SIZE);
            DeploymentId = GetStringSetting(properties, DEPLOYMENT_ID_NAME, DEFAULT_DEPLOYMENT_ID);
            CacheSizeKb = GetIntSetting(properties, CACHE_SIZE_KB_NAME, DEFAULT_CACHE_SIZE_KB);
        }

        public string StreamProvider { get; set; }

        /// <summary>
        /// Total number of queues across all silos
        /// </summary>
        public int TotalQueueCount { get; private set; }

        /// <summary>
        /// Number of streams to generate events on per queue
        /// </summary>
        /// <remarks>
        /// This means that the total number of streams is QueueCount * NumStreamsPerQueue
        /// </remarks>
        public int NumStreamsPerQueue { get; private set; }

        /// <summary>
        /// Message producer to use
        /// </summary>
        public string MessageProducer { get; set; }

        /// <summary>
        /// By default, there is one implicit subscriber per stream. This specifies how many
        /// additional explicit subscriptions to set up.
        /// </summary>
        public int AdditionalSubscribersCount { get; set; }

        /// <summary>
        /// How long to Task.Delay during activation, in ms.
        /// </summary>
        public int ActivationTaskDelay { get; set; }

        /// <summary>
        /// How long to while(true) {} loop during activation, in ms.
        /// </summary>
        public int ActivationBusyWait { get; set; }

        /// <summary>
        /// How long to Task.Delay during processing each event, in ms.
        /// </summary>
        public int EventTaskDelay { get; set; }

        /// <summary>
        /// How long to while(true) {} loop during processing each event, in ms.
        /// </summary>
        public int EventBusyWait { get; set; }

        /// <summary>
        /// How long to wait before creating subscriptions. 
        /// </summary>
        /// <remarks>
        /// This prevents creating additional subscriptions because initially, one silo will own all
        /// event generation before other silos are spun up and agents are load balanced to them. If
        /// we didn't wait before generating subscriptions, since we don't transition state between
        /// the agents, we would have extra orphaned subscriptions.  By waiting a few minutes, this
        /// fixes the issue.
        /// </remarks>
        public int SiloStabilizationTime { get; set; }

        /// <summary>
        /// How long to wait in between starting each new subscription, in ms.
        /// </summary>
        public int RampUpStagger { get; set; }

        /// <summary>
        /// How long a subscription should last before being unsubscribed, in ms.
        /// </summary>
        public int SubscriptionLength { get; set; }

        /// <summary>
        /// Target events per second per stream.
        /// </summary>
        public int StreamEventsPerSecond { get; set; }

        /// <summary>
        /// Target number of batches to send per second.
        /// </summary>
        public int TargetBatchesSentPerSecond { get; set; }

        /// <summary>
        /// Maximum number of batches to return from a request.
        /// </summary>
        public int MaxBatchesPerRequest { get; set; }

        /// <summary>
        /// Maximum number of events to put in a batch.
        /// </summary>
        /// <remarks>
        /// This means that the max total number of events per request is MaxBatchesPerRequest * MaxEventsPerBatch.
        /// </remarks>
        public int MaxEventsPerBatch { get; set; }

        /// <summary>
        /// Additional buffer size to attach to events, in bytes.
        /// </summary>
        public int EventSize { get; set; }

        /// <summary>
        /// Used to distinguish persistent data from different test runs.
        /// </summary>
        public string DeploymentId { get; set; }

        /// <summary>
        /// Size of the cache, in KB.
        /// </summary>
        public int CacheSizeKb { get; set; }
        
        private static int GetIntSetting(IDictionary<string, string> properties, string settingName, int settingDefault)
        {
            string s;
            return properties.TryGetValue(settingName, out s) ? int.Parse(s) : settingDefault;
        }

        private static string GetStringSetting(IDictionary<string, string> properties, string settingName, string settingDefault)
        {
            string s;
            return properties.TryGetValue(settingName, out s) ? s : settingDefault;
        }

        private static TimeSpan GetTimespanSetting(IDictionary<string, string> properties, string settingName, TimeSpan settingDefault)
        {
            string s;
            return properties.TryGetValue(settingName, out s) ? TimeSpan.FromMilliseconds(int.Parse(s)) : settingDefault;
        }
    }

    internal interface IStreamNamespaceMessageProducer
    {
        Task<IBatchContainer[]> GetQueueMessagesAsync(int targetBatchesPerSecond = -1);
    }

    internal class ReentrantImplicitConsumerMessageProducer : IStreamNamespaceMessageProducer
    {
        private const string StreamNamespace = "ReentrantImplicitConsumer";

        private readonly MockStreamProviderSettings _settings;
        private readonly List<Guid> _streamGuids;
        private int _nextStreamGuidIndex;

        public ReentrantImplicitConsumerMessageProducer(MockStreamProviderSettings settings, QueueId queueId)
        {
            _settings = settings;
            _nextStreamGuidIndex = 0;
            _streamGuids = new List<Guid>();
            for (int i = 0; i < _settings.NumStreamsPerQueue; i++)
            {
                _streamGuids.Add(MakeStreamGuid(queueId, i));
            }
        }

        public Task<IBatchContainer[]> GetQueueMessagesAsync(int targetBatchesPerSecond = -1)
        {
            List<StreamItem> items = new List<StreamItem>(); // Enumerable.Range((int)first, BatchSize).Select(i => (long)i);
            for (int i = 0; i < _settings.MaxBatchesPerRequest; i++)
            {
                StreamItem item = new StreamItem(new byte[100]);
                items.Add(item);
            }
            IBatchContainer[] batchContainer = { new BatchContainer<StreamItem>(_streamGuids[_nextStreamGuidIndex], StreamNamespace, items) };
            _nextStreamGuidIndex = (_nextStreamGuidIndex + 1) % _streamGuids.Count;
            return Task.FromResult(batchContainer);
        }

        internal Guid MakeStreamGuid(QueueId id, int ordinal)
        {
            int hashCode = unchecked((int)(((uint)id.GetHashCode()) + ((uint)_settings.DeploymentId.GetHashCode())));
            return new Guid(hashCode, 0, checked((byte)ordinal), 0, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    internal class ImplicitConsumerMessageProducer : IStreamNamespaceMessageProducer
    {
        private const string StreamNamespace = "ImplicitConsumer";
        
        private readonly MockStreamProviderSettings _settings;
        private readonly MessageProducerStream[] _streams;
        private readonly DateTime _startTime;
        private int _nextStreamIndex;
        private int _totalEventsSent;

        public ImplicitConsumerMessageProducer(MockStreamProviderSettings settings, Logger logger)
        {
            _settings = settings;
            
            _streams = Enumerable
                .Range(0, _settings.NumStreamsPerQueue)
                .Select(i => new MessageProducerStream(logger, _settings, i))
                .ToArray();

            _startTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(_settings.SiloStabilizationTime);
        }

        public Task<IBatchContainer[]> GetQueueMessagesAsync(int targetBatchesPerSecond = -1)
        {
            if (targetBatchesPerSecond == -1)
        {
                targetBatchesPerSecond = _settings.StreamEventsPerSecond * _settings.NumStreamsPerQueue;
            }
            double secondsSinceStart = Math.Max((DateTime.UtcNow - _startTime).TotalSeconds, 0);
            int targetTotalEventsSent = (int)Math.Floor(secondsSinceStart * targetBatchesPerSecond);
            int eventCountToSend = targetTotalEventsSent - _totalEventsSent;
            int eventCountToSendThisBatch = Math.Min(eventCountToSend, _settings.MaxBatchesPerRequest * _settings.MaxEventsPerBatch);

            if (eventCountToSendThisBatch == 0)
            {
                return Task.FromResult(new IBatchContainer[0]);
            }

            var batchContainersList = new List<BatchContainer<StreamingLoadTestBaseEvent>>();
            var batchContainersDictionary = new Dictionary<Guid, BatchContainer<StreamingLoadTestBaseEvent>>();

            int nonDummyEventCount = 0;
            for (int i = 0; i < eventCountToSendThisBatch; i++, _nextStreamIndex = (_nextStreamIndex + 1) % _streams.Length)
            {
                MessageProducerStream stream = _streams[_nextStreamIndex];

                Tuple<Guid, StreamingLoadTestBaseEvent> item = stream.GenerateEvent();
                if (item != null)
                {
                    nonDummyEventCount++;

                    BatchContainer<StreamingLoadTestBaseEvent> batchContainer;
                    if (batchContainersDictionary.TryGetValue(item.Item1, out batchContainer))
                    {
                        batchContainer.AddEvent(item.Item2);
                    }
                    else
                    {
                        batchContainer = new BatchContainer<StreamingLoadTestBaseEvent>(
                            item.Item1,
                            StreamNamespace,
                            new List<StreamingLoadTestBaseEvent> { item.Item2 });
                        batchContainersList.Add(batchContainer);
                        batchContainersDictionary.Add(item.Item1, batchContainer);
                    }
                }
            }

            _totalEventsSent += eventCountToSendThisBatch;
            return Task.FromResult(batchContainersList.AsEnumerable<IBatchContainer>().ToArray());
        }
    }

    internal class MessageProducerStream
    {
        private readonly MockStreamProviderSettings _settings;
        private readonly Logger _logger;
        private readonly int EventCount;
        private readonly int DummyEventCount;


        private Guid _streamId;
        private int _dummyEventCount;
        private bool _started;
        private int _eventCount;
        
        public MessageProducerStream(Logger logger, MockStreamProviderSettings settings, int index)
        {
            _logger = logger;
            _settings = settings;
            EventCount = (int)(TimeSpan.FromMilliseconds(_settings.SubscriptionLength).TotalSeconds * _settings.StreamEventsPerSecond);
            DummyEventCount = (int)(TimeSpan.FromMilliseconds(_settings.RampUpStagger).TotalSeconds * _settings.StreamEventsPerSecond);

            _streamId = Guid.NewGuid();
            _dummyEventCount = DummyEventCount * index;
            _started = false;
            _eventCount = EventCount;
        }

        public Tuple<Guid, StreamingLoadTestBaseEvent> GenerateEvent()
        {
            Tuple<Guid, StreamingLoadTestBaseEvent> item;
            if (_dummyEventCount > 0)
            {
                item = null;
                _dummyEventCount--;
            }
            else if (_dummyEventCount == 0 && !_started)
            {
                item = new Tuple<Guid, StreamingLoadTestBaseEvent>(
                    _streamId,
                    new StreamingLoadTestStartEvent
                    {
                        TaskDelayMs = _settings.ActivationTaskDelay,
                        BusyWaitMs = _settings.ActivationBusyWait,
                        Data = new byte[_settings.EventSize],
                        StreamProvider = _settings.StreamProvider,
                        AdditionalSubscribersCount = _settings.AdditionalSubscribersCount
                    });

                if (_logger.IsVerbose)
                {
                    _logger.Verbose("Sending START event on stream id {0}", _streamId);
                }

                _started = true;
            }
            else if (_eventCount > 0)
            {
                item = new Tuple<Guid, StreamingLoadTestBaseEvent>(
                    _streamId,
                    new StreamingLoadTestEvent
                    {
                        TaskDelayMs = _settings.EventTaskDelay,
                        BusyWaitMs = _settings.EventBusyWait,
                        Data = new byte[_settings.EventSize]
                    });

                _eventCount--;
            }
            else
            {
                item = new Tuple<Guid, StreamingLoadTestBaseEvent>(
                    _streamId,
                    new StreamingLoadTestEndEvent
                    {
                        TaskDelayMs = _settings.EventTaskDelay,
                        BusyWaitMs = _settings.EventBusyWait,
                        Data = new byte[_settings.EventSize]
                    });

                if (_logger.IsVerbose)
                {
                    _logger.Verbose("Sending STOP event on stream id {0}", _streamId);
                }

                _streamId = Guid.NewGuid();
                _dummyEventCount = 0;
                _started = false;
                _eventCount = EventCount;
            }

            return item;
        }
    }

    [Serializable]
    internal class BatchContainer<TEvent> : IBatchContainer
    {
        private readonly IList<TEvent> _events;

        public Guid StreamGuid { get; private set; }

        public string StreamNamespace { get; private set; }

        public StreamSequenceToken SequenceToken { get; private set; }

        public IReadOnlyList<TEvent> Events { get; private set; }

        public BatchContainer(Guid streamGuid, String streamNamespace, List<TEvent> events, StreamSequenceToken startToken = null)
        {
            if (null == events)
            {
                throw new ArgumentNullException("events");
            }

            StreamGuid = streamGuid;
            StreamNamespace = streamNamespace;
            SequenceToken = startToken;
            _events = events;
            Events = events.AsReadOnly();
        }

        public void AddEvent(TEvent item)
        {
            _events.Add(item);
        }

        public IEnumerable<Tuple<T,StreamSequenceToken>> GetEvents<T>()
        {
            return _events.Cast<T>().Select(e => Tuple.Create<T, StreamSequenceToken>(e, null));
        }

        public bool ShouldDeliver(
            IStreamIdentity stream,
            object filterData,
            StreamFilterPredicate shouldDeliverFunc)
        {
            foreach (object item in _events)
            {
                if (shouldDeliverFunc(stream, filterData, item))
                {
                    // There is something in this batch that the consumer is intereted in, so we should send it.
                    return true;
                }
            }
            return false; // Consumer is not interested in any of these events, so don't send.
        }
    }
}
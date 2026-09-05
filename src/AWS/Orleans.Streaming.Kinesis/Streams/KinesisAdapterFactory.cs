using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Kinesis;
using Amazon.Kinesis.Model;
using Amazon.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Streaming.Kinesis
{
    /// <summary>
    /// Queue adapter factory which allows the PersistentStreamProvider to use AWS Kinesis Data Streams as its backend persistent event queue.
    /// </summary>
    internal class KinesisAdapterFactory : IQueueAdapterFactory, IQueueAdapter, IQueueAdapterCache, IDisposable
    {
        private readonly KinesisStreamOptions _options;
        private readonly Serializer<KinesisBatchContainer.Body> _serializer;
        private readonly IStreamQueueCheckpointerFactory? _checkpointerFactory;
        private readonly ILoggerFactory _loggerFactory;
        private readonly SimpleQueueCacheOptions _cacheOptions;
        private readonly ILogger<KinesisAdapterFactory> _logger;
        private readonly Func<string[], HashRingBasedPartitionedStreamQueueMapper> _queueMapperFactory;
        private readonly IAmazonKinesis _client;
        private readonly TimeProvider _timeProvider;
        private readonly SemaphoreSlim _initializationSemaphore = new(1);

        private InitializationState? _initializationState;
        private int _disposed;

        public KinesisAdapterFactory(
            string name,
            KinesisStreamOptions options,
            SimpleQueueCacheOptions cacheOptions,
            Serializer<KinesisBatchContainer.Body> serializer,
            IStreamQueueCheckpointerFactory? checkpointerFactory,
            ILoggerFactory loggerFactory,
            TimeProvider? timeProvider = null
        )
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            Name = name;
            _serializer = serializer;
            _checkpointerFactory = checkpointerFactory;
            _loggerFactory = loggerFactory;
            _cacheOptions = cacheOptions;
            _logger = loggerFactory.CreateLogger<KinesisAdapterFactory>();
            _timeProvider = timeProvider ?? TimeProvider.System;

            _queueMapperFactory = partitions => new HashRingBasedPartitionedStreamQueueMapper(partitions, Name);
            _client = CreateClient();
        }

        public string Name { get; }

        public bool IsRewindable => true;

        public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

        public static KinesisAdapterFactory Create(IServiceProvider services, string name)
        {
            var streamsConfig = services.GetOptionsByName<KinesisStreamOptions>(name);
            var cacheOptions = services.GetOptionsByName<SimpleQueueCacheOptions>(name);
            var serializer = services.GetRequiredService<Serializer<KinesisBatchContainer.Body>>();
            var checkpointerFactory = services.GetKeyedService<IStreamQueueCheckpointerFactory>(name);
            var logger = services.GetRequiredService<ILoggerFactory>();

            return new KinesisAdapterFactory(
                name,
                streamsConfig,
                cacheOptions,
                serializer,
                checkpointerFactory,
                logger,
                services.GetService<TimeProvider>());
        }

        public async Task<IQueueAdapter> CreateAdapter()
            => await CreateAdapter(CancellationToken.None);

        public async Task<IQueueAdapter> CreateAdapter(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _initializationState) is not null)
            {
                return this;
            }

            await _initializationSemaphore.WaitAsync(cancellationToken);
            try
            {
                if (_initializationState is not null)
                {
                    return this;
                }

                var partitions = await GetPartitionIdsAsync(cancellationToken);
                var queueMapper = _queueMapperFactory(partitions);
                var topologyMonitor = new KinesisShardTopologyMonitor(
                    _client,
                    _options.StreamName,
                    partitions,
                    _options.TopologyCheckInterval,
                    _timeProvider,
                    _loggerFactory.CreateLogger<KinesisShardTopologyMonitor>());
                QueueAdapterReceiverRegistry<KinesisPooledAdapterReceiver>? receivers = null;
                receivers = new(
                    queueId => MakeReceiver(queueId, queueMapper, topologyMonitor, receivers!));

                Volatile.Write(ref _initializationState, new(queueMapper, receivers));
                return this;
            }
            finally
            {
                _initializationSemaphore.Release();
            }
        }

        public IQueueAdapterCache GetQueueAdapterCache()
            => this;

        public IStreamQueueMapper GetStreamQueueMapper()
            => GetInitializationState().StreamQueueMapper;

        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
            => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler(false));

        public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken? token, Dictionary<string, object>? requestContext)
        {
            var data = KinesisBatchContainer.ToKinesisPayload(_serializer, streamId, events, requestContext);

            var putRecordRequest = new PutRecordRequest
            {
                StreamName = _options.StreamName,
                Data = new MemoryStream(data),
                PartitionKey = streamId.GetKeyAsString(),
            };

            _ = await _client.PutRecordAsync(putRecordRequest);
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
            => GetOrCreateReceiver(queueId);

        public IQueueCache CreateQueueCache(QueueId queueId)
            => GetOrCreateReceiver(queueId);

        private KinesisPooledAdapterReceiver GetOrCreateReceiver(QueueId queueId)
        {
            if (_checkpointerFactory is null)
            {
                throw new OrleansConfigurationException(
                    $"No {nameof(IStreamQueueCheckpointerFactory)} is configured for the Kinesis stream provider '{Name}'.");
            }

            return GetInitializationState().Receivers.GetOrCreate(queueId);
        }

        private KinesisPooledAdapterReceiver MakeReceiver(
            QueueId queueId,
            HashRingBasedPartitionedStreamQueueMapper streamQueueMapper,
            KinesisShardTopologyMonitor topologyMonitor,
            QueueAdapterReceiverRegistry<KinesisPooledAdapterReceiver> receivers)
        {
            var partition = streamQueueMapper.QueueToPartition(queueId);
            return new KinesisPooledAdapterReceiver(
                CreateClient(),
                _options.StreamName,
                partition,
                _checkpointerFactory!,
                _cacheOptions,
                _serializer,
                _loggerFactory,
                topologyMonitor,
                _options.GetRecordsInterval,
                _timeProvider,
                receiver => receivers.Remove(queueId, receiver)
                );
        }

        private InitializationState GetInitializationState()
            => Volatile.Read(ref _initializationState)
                ?? throw new InvalidOperationException(
                    $"The Kinesis stream provider '{Name}' must complete {nameof(CreateAdapter)} before accessing initialized adapter state.");

        internal IAmazonKinesis CreateClient() => CreateClient(_options);

        internal static IAmazonKinesis CreateClient(KinesisStreamOptions options)
        {
            if (options.Service != null && (options.Service.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                options.Service.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                var config = new AmazonKinesisConfig
                {
                    ServiceURL = options.Service,
                    AuthenticationRegion = GetRegionName(options),
                };

                return !string.IsNullOrEmpty(options.AccessKey) && !string.IsNullOrEmpty(options.SecretKey)
                    ? new AmazonKinesisClient(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config)
                    : new AmazonKinesisClient(config);
            }

            var regionName = GetRegionName(options);
            var awsConfig = new AmazonKinesisConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(regionName) };
            if (!string.IsNullOrEmpty(options.AccessKey) && !string.IsNullOrEmpty(options.SecretKey))
            {
                return new AmazonKinesisClient(new BasicAWSCredentials(options.AccessKey, options.SecretKey), awsConfig);
            }

            return new AmazonKinesisClient(awsConfig);
        }

        internal static string GetRegionName(KinesisStreamOptions options)
        {
            if (!string.IsNullOrEmpty(options.Region))
            {
                return options.Region;
            }

            if (Uri.TryCreate(options.Service, UriKind.Absolute, out var serviceUri))
            {
                var hostSegments = serviceUri.Host.Split('.');
                if (hostSegments is [var service, var region, ..]
                    && service.StartsWith("kinesis", StringComparison.OrdinalIgnoreCase)
                    && region.Contains('-', StringComparison.Ordinal))
                {
                    return region;
                }
            }

            return "us-east-1";
        }

        internal async Task<string[]> GetPartitionIdsAsync()
            => await GetPartitionIdsAsync(CancellationToken.None);

        internal virtual async Task<string[]> GetPartitionIdsAsync(CancellationToken cancellationToken)
            => await GetPartitionIdsAsync(_client, _options.StreamName, cancellationToken);

        internal static Task<string[]> GetPartitionIdsAsync(IAmazonKinesis client, string streamName)
            => GetPartitionIdsAsync(client, streamName, CancellationToken.None);

        internal static async Task<string[]> GetPartitionIdsAsync(
            IAmazonKinesis client,
            string streamName,
            CancellationToken cancellationToken)
        {
            var partitions = new HashSet<string>(StringComparer.Ordinal);
            string? nextToken = null;
            do
            {
                var request = nextToken is null
                    ? new ListShardsRequest { StreamName = streamName }
                    : new ListShardsRequest { NextToken = nextToken };

                var response = await client.ListShardsAsync(request, cancellationToken);
                foreach (var shard in response.Shards ?? [])
                {
                    partitions.Add(shard.ShardId);
                }

                nextToken = response.NextToken;
            }
            while (!string.IsNullOrEmpty(nextToken));

            return [.. partitions.Order(StringComparer.Ordinal)];
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _client.Dispose();
            }
        }

        private sealed record InitializationState(
            HashRingBasedPartitionedStreamQueueMapper StreamQueueMapper,
            QueueAdapterReceiverRegistry<KinesisPooledAdapterReceiver> Receivers);
    }
}

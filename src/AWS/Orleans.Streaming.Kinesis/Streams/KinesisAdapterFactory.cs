using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    public class KinesisAdapterFactory : IQueueAdapterFactory, IQueueAdapter
    {
        private readonly KinesisStreamOptions _options;
        private readonly Serializer _serializer;
        private readonly IStreamQueueCheckpointerFactory _checkpointerFactory;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IQueueAdapterCache _adapterCache;
        private readonly ILogger<KinesisAdapterFactory> _logger;
        private readonly Func<string[], HashRingBasedPartitionedStreamQueueMapper> _queueMapperFactory;
        private readonly AmazonKinesisClient _client;

        private HashRingBasedPartitionedStreamQueueMapper _streamQueueMapper;

        public KinesisAdapterFactory(
            string name,
            KinesisStreamOptions options,
            SimpleQueueCacheOptions cacheOptions,
            Serializer serializer,
            IStreamQueueCheckpointerFactory checkpointerFactory,
            ILoggerFactory loggerFactory
        )
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            Name = name;
            _serializer = serializer;
            _checkpointerFactory = checkpointerFactory;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<KinesisAdapterFactory>();

            _adapterCache = new SimpleQueueAdapterCache(
                cacheOptions,
                name,
                loggerFactory
            );

            _queueMapperFactory = partitions => new HashRingBasedPartitionedStreamQueueMapper(partitions, Name);
            _client = CreateClient();
        }

        public string Name { get; }

        public bool IsRewindable => false;

        public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

        public static KinesisAdapterFactory Create(IServiceProvider services, string name)
        {
            var streamsConfig = services.GetOptionsByName<KinesisStreamOptions>(name);
            var cacheOptions = services.GetOptionsByName<SimpleQueueCacheOptions>(name);
            var serializer = services.GetRequiredService<Serializer>();
            var logger = services.GetRequiredService<ILoggerFactory>();
            var grainFactory = services.GetRequiredService<IGrainFactory>();
            var checkpointerFactory = services.GetRequiredKeyedService<IStreamQueueCheckpointerFactory>(name);

            var factory = ActivatorUtilities.CreateInstance<KinesisAdapterFactory>(
                    services,
                    name,
                    streamsConfig,
                    cacheOptions,
                    serializer,
                    checkpointerFactory,
                    logger,
                    grainFactory,
                    services
                );

            return factory;
        }

        public async Task<IQueueAdapter> CreateAdapter()
        {
            if (_streamQueueMapper is null)
            {
                var kinesisStreams = await GetPartitionIdsAsync();
                _streamQueueMapper = _queueMapperFactory(kinesisStreams);
            }

            return this;
        }

        public IQueueAdapterCache GetQueueAdapterCache()
            => _adapterCache;

        public IStreamQueueMapper GetStreamQueueMapper()
            => _streamQueueMapper;

        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
            => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler(false));

        public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
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
        {
            var partition = _streamQueueMapper.QueueToPartition(queueId);

            return new KinesisAdapterReceiver(
                CreateClient(),
                _options.StreamName,
                partition,
                _checkpointerFactory,
                _serializer,
                _loggerFactory
                );
        }

        private AmazonKinesisClient CreateClient()
        {
            if (_options.Service.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                _options.Service.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // Local Kinesis instance (for testing)
                var credentials = !string.IsNullOrEmpty(_options.AccessKey) && !string.IsNullOrEmpty(_options.SecretKey) ?
                    new BasicAWSCredentials(_options.AccessKey, _options.SecretKey) :
                    new BasicAWSCredentials("dummy", "dummyKey");

                return new AmazonKinesisClient(credentials, new AmazonKinesisConfig { ServiceURL = _options.Service });
            }
            else if (!string.IsNullOrEmpty(_options.AccessKey) && !string.IsNullOrEmpty(_options.SecretKey))
            {
                // AWS Kinesis instance (auth via explicit credentials)
                var credentials = new BasicAWSCredentials(_options.AccessKey, _options.SecretKey);
                return new AmazonKinesisClient(credentials, new AmazonKinesisConfig { RegionEndpoint = AWSUtils.GetRegionEndpoint(_options.Service) });
            }
            else
            {
                // AWS Kinesis instance (implicit auth - EC2 IAM Roles etc)
                return new AmazonKinesisClient(new AmazonKinesisConfig { RegionEndpoint = AWSUtils.GetRegionEndpoint(_options.Service) });
            }
        }

        private async Task<string[]> GetPartitionIdsAsync()
        {
            var request = new ListShardsRequest
            {
                StreamName = _options.StreamName,
            };

            var response = await _client.ListShardsAsync(request);

            return response.Shards.Select(s => s.ShardId).ToArray();
        }
    }
}

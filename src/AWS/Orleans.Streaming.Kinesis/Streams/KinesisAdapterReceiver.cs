using Amazon.Kinesis;
using Amazon.Kinesis.Model;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Streaming.Kinesis
{
    internal class KinesisAdapterReceiver : IQueueAdapterReceiver
    {
        private readonly ILogger<KinesisAdapterReceiver> _logger;
        private readonly AmazonKinesisClient _client;
        private readonly string _streamName;
        private readonly string _partition;
        private readonly IStreamQueueCheckpointerFactory _checkpointerFactory;
        private readonly Serializer<KinesisBatchContainer.Body> _serializer;

        private IStreamQueueCheckpointer<string> _checkpointer;
        private string _shardIterator;
        private long _lastReadMessage;

        internal KinesisAdapterReceiver(
            AmazonKinesisClient client,
            string streamName,
            string partition,
            IStreamQueueCheckpointerFactory checkpointerFactory,
            Serializer<KinesisBatchContainer.Body> serializer,
            ILoggerFactory loggerFactory
            )
        {
            _client = client;
            _streamName = streamName;
            _partition = partition;
            _checkpointerFactory = checkpointerFactory;
            _serializer = serializer;
            _logger = loggerFactory.CreateLogger<KinesisAdapterReceiver>();
        }

        public async Task Initialize(TimeSpan timeout)
        {
            _checkpointer = await _checkpointerFactory.Create(_partition);
            var checkpointOffset = await _checkpointer.Load();

            var getShardIteratorRequest = new GetShardIteratorRequest
            {
                StreamName = _streamName,
                ShardId = _partition,
            };

            if (string.IsNullOrEmpty(checkpointOffset))
            {
                getShardIteratorRequest.ShardIteratorType = ShardIteratorType.TRIM_HORIZON;
            }
            else
            {
                getShardIteratorRequest.ShardIteratorType = ShardIteratorType.AFTER_SEQUENCE_NUMBER;
                getShardIteratorRequest.StartingSequenceNumber = checkpointOffset;
            }

            var getShardIteratorResponse = await _client.GetShardIteratorAsync(getShardIteratorRequest);
            _shardIterator = getShardIteratorResponse.ShardIterator;
        }

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
        {
            var getRecordsRequest = new GetRecordsRequest
            {
                Limit = maxCount,
                ShardIterator = _shardIterator,
            };

            var getRecordsResponse = await _client.GetRecordsAsync(getRecordsRequest);
            _shardIterator = getRecordsResponse.NextShardIterator;

            if (getRecordsResponse.Records.Count == 0)
            {
                return Array.Empty<IBatchContainer>();
            }

            var batch = new List<IBatchContainer>();

            foreach (var record in getRecordsResponse.Records)
            {
                // Kinesis only has a long string sequence ID, so we fake one based on the order we read from the partition.
                batch.Add(KinesisBatchContainer.FromKinesisRecord(_serializer, record, _lastReadMessage++));
            }

            return batch;
        }

        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            KinesisBatchContainer batchWithHighestOffset = null;

            try
            {
                if (!messages.Any())
                    return Task.CompletedTask;

                batchWithHighestOffset = messages
                    .Cast<KinesisBatchContainer>()
                    .Max();

                _checkpointer.Update(batchWithHighestOffset.Token.ShardSequence, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to commit message offset {@offset} to shard {shardId}", batchWithHighestOffset?.Token?.ShardSequence, _partition);
                throw;
            }

            return Task.CompletedTask;
        }

        public Task Shutdown(TimeSpan timeout)
        {
            return Task.CompletedTask;
        }
    }
}

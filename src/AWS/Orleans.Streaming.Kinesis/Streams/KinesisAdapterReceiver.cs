using Amazon.Kinesis;
using Amazon.Kinesis.Model;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Streaming.Kinesis
{
    internal class KinesisAdapterReceiver : IQueueAdapterReceiver
    {
        private readonly ILogger<KinesisAdapterReceiver> _logger;
        private readonly IAmazonKinesis _client;
        private readonly string _streamName;
        private readonly string _partition;
        private readonly IStreamQueueCheckpointerFactory _checkpointerFactory;
        private readonly Serializer<KinesisBatchContainer.Body> _serializer;
        private readonly KinesisShardTopologyMonitor _topologyMonitor;
        private readonly TimeSpan _getRecordsInterval;
        private readonly TimeProvider _timeProvider;

        private IStreamQueueCheckpointer<string> _checkpointer = null!;
        private string _shardIterator = null!;
        private long _lastReadMessage;
        private DateTimeOffset _nextGetRecordsUtc;
        private bool _shardExhausted;
        private bool _topologyCheckRequired;
        private bool _initialized;

        internal KinesisAdapterReceiver(
            IAmazonKinesis client,
            string streamName,
            string partition,
            IStreamQueueCheckpointerFactory checkpointerFactory,
            Serializer<KinesisBatchContainer.Body> serializer,
            ILoggerFactory loggerFactory,
            KinesisShardTopologyMonitor topologyMonitor,
            TimeSpan getRecordsInterval,
            TimeProvider timeProvider
            )
        {
            _client = client;
            _streamName = streamName;
            _partition = partition;
            _checkpointerFactory = checkpointerFactory;
            _serializer = serializer;
            _logger = loggerFactory.CreateLogger<KinesisAdapterReceiver>();
            _topologyMonitor = topologyMonitor;
            _getRecordsInterval = getRecordsInterval;
            _timeProvider = timeProvider;
        }

        public async Task Initialize(TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            await Initialize(cancellation.Token);
        }

        private async Task Initialize(CancellationToken cancellationToken)
        {
            if (_initialized)
            {
                return;
            }

            _checkpointer = await _checkpointerFactory.Create(_partition, cancellationToken);
            await ResetShardIterator(cancellationToken);
            _initialized = true;
        }

        private async Task ResetShardIterator(CancellationToken cancellationToken)
        {
            var checkpointOffset = await _checkpointer.Load(cancellationToken);

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

            var getShardIteratorResponse = await _client.GetShardIteratorAsync(
                getShardIteratorRequest,
                cancellationToken);
            _shardIterator = getShardIteratorResponse.ShardIterator;
            _shardExhausted = string.IsNullOrEmpty(_shardIterator);
        }

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
            => await GetQueueMessagesAsync(maxCount, CancellationToken.None);

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(
            int maxCount,
            CancellationToken cancellationToken)
        {
            if (!_initialized)
            {
                await Initialize(cancellationToken);
            }

            if (!await _topologyMonitor.CheckTopology(_topologyCheckRequired, cancellationToken))
            {
                return Array.Empty<IBatchContainer>();
            }

            _topologyCheckRequired = false;
            if (_shardExhausted)
            {
                return Array.Empty<IBatchContainer>();
            }

            await WaitForGetRecordsInterval(cancellationToken);
            var getRecordsRequest = new GetRecordsRequest
            {
                Limit = maxCount,
                ShardIterator = _shardIterator,
            };

            GetRecordsResponse getRecordsResponse;
            try
            {
                getRecordsResponse = await _client.GetRecordsAsync(getRecordsRequest, cancellationToken);
            }
            catch (ExpiredIteratorException)
            {
                await ResetShardIterator(cancellationToken);
                if (_shardExhausted)
                {
                    return Array.Empty<IBatchContainer>();
                }

                await WaitForGetRecordsInterval(cancellationToken);
                getRecordsRequest.ShardIterator = _shardIterator;
                getRecordsResponse = await _client.GetRecordsAsync(getRecordsRequest, cancellationToken);
            }

            _shardIterator = getRecordsResponse.NextShardIterator;
            if (string.IsNullOrEmpty(_shardIterator))
            {
                _shardExhausted = true;
                _topologyCheckRequired = true;
            }

            if (getRecordsResponse.Records is not { Count: > 0 })
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

        private async Task WaitForGetRecordsInterval(CancellationToken cancellationToken)
        {
            var delay = _nextGetRecordsUtc - _timeProvider.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _timeProvider, cancellationToken);
            }

            _nextGetRecordsUtc = _timeProvider.GetUtcNow() + _getRecordsInterval;
        }

        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
            => MessagesDeliveredAsync(messages, CancellationToken.None);

        public Task MessagesDeliveredAsync(
            IList<IBatchContainer> messages,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KinesisBatchContainer? batchWithHighestOffset = null;

            try
            {
                if (!messages.Any())
                    return Task.CompletedTask;

                batchWithHighestOffset = messages
                    .Cast<KinesisBatchContainer>()
                    .Max() ?? throw new InvalidOperationException("Delivered messages contained no Kinesis batches.");

                _checkpointer.Update(
                    batchWithHighestOffset.Token.ShardSequence,
                    DateTime.UtcNow,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to commit message offset {@offset} to shard {shardId}", batchWithHighestOffset?.Token?.ShardSequence, _partition);
                throw;
            }

            return Task.CompletedTask;
        }

        public async Task Shutdown(TimeSpan timeout)
        {
            try
            {
                if (_initialized)
                {
                    using var cancellation = new CancellationTokenSource(timeout);
                    await _checkpointer.FlushAsync(cancellation.Token);
                }
            }
            finally
            {
                _client.Dispose();
            }
        }
    }
}

using Amazon.Kinesis;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Streaming.Kinesis
{
    internal sealed partial class KinesisShardTopologyMonitor
    {
        private readonly IAmazonKinesis _client;
        private readonly string _streamName;
        private readonly HashSet<string> _initialShards;
        private readonly TimeSpan _checkInterval;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);

        private DateTimeOffset _nextCheckUtc;
        private volatile bool _healthy = true;

        public KinesisShardTopologyMonitor(
            IAmazonKinesis client,
            string streamName,
            IEnumerable<string> initialShards,
            TimeSpan checkInterval,
            TimeProvider timeProvider,
            ILogger<KinesisShardTopologyMonitor> logger)
        {
            _client = client;
            _streamName = streamName;
            _initialShards = initialShards.ToHashSet(StringComparer.Ordinal);
            _checkInterval = checkInterval;
            _timeProvider = timeProvider;
            _logger = logger;
            _nextCheckUtc = timeProvider.GetUtcNow() + checkInterval;
        }

        public async Task<bool> CheckTopology(
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            if (!_healthy)
            {
                return false;
            }

            if (!force && _timeProvider.GetUtcNow() < _nextCheckUtc)
            {
                return true;
            }

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (!_healthy)
                {
                    return false;
                }

                if (!force && _timeProvider.GetUtcNow() < _nextCheckUtc)
                {
                    return true;
                }

                var currentShards = await KinesisAdapterFactory.GetPartitionIdsAsync(
                    _client,
                    _streamName,
                    cancellationToken);
                if (!_initialShards.SetEquals(currentShards))
                {
                    _healthy = false;
                    LogTopologyChanged(
                        _logger,
                        _streamName,
                        string.Join(", ", _initialShards.Order(StringComparer.Ordinal)),
                        string.Join(", ", currentShards));
                    return false;
                }

                _nextCheckUtc = _timeProvider.GetUtcNow() + _checkInterval;
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        [LoggerMessage(
            Level = LogLevel.Critical,
            Message = "Kinesis stream '{StreamName}' shard topology changed after provider initialization. Initial shards: [{InitialShards}]. Current shards: [{CurrentShards}]. Live resharding is not supported; Kinesis receivers have stopped. Restart the Orleans stream provider to resume consumption.")]
        private static partial void LogTopologyChanged(ILogger logger, string streamName, string initialShards, string currentShards);
    }
}

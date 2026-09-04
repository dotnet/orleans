using System.Data.Common;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Orleans.Providers.Streams.Common;

namespace Orleans.Streaming.AdoNet;

internal sealed partial class AdoNetRecoverableStream(
    string serviceId,
    string providerId,
    string queueId,
    AdoNetStreamOptions options,
    RelationalOrleansQueries queries,
    ILogger logger,
    TimeProvider? timeProvider = null)
    : IRecoverableStreamSource<AdoNetStreamMessage>,
      IRecoverableStreamReplaySourceFactory<AdoNetStreamMessage>,
      IStreamCheckpointStore
{
    private AdoNetStreamPartitionState? _partition;
    private long _readOffset;
    private Task<AdoNetStreamPartitionState>? _acquisitionTask;
    private DataNotAvailableException? _retentionFailure;
    private long? _pendingHardDeletedThrough;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal Task AcquisitionCompletion => Volatile.Read(ref _acquisitionTask) ?? Task.CompletedTask;

    public async ValueTask<StreamCheckpointStoreState> Load(CancellationToken cancellationToken)
    {
        var acquisitionTask = queries.AcquireStreamPartitionAsync(
            serviceId,
            providerId,
            queueId,
            options.StartFromNow,
            cancellationToken);
        Volatile.Write(ref _acquisitionTask, acquisitionTask);
        var partition = await acquisitionTask;
        cancellationToken.ThrowIfCancellationRequested();
        _partition = partition;
        _readOffset = partition.Checkpoint ?? 0;
        ThrowIfRetentionGap(partition);
        var checkpoint = partition.Checkpoint?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        return new(checkpoint, partition.OwnerEpoch.ToString(CultureInfo.InvariantCulture));
    }

    public async ValueTask<StreamCheckpointStoreState> Update(
        string checkpoint,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        if (_partition is not { } partition)
        {
            throw new InvalidOperationException("The ADO.NET stream partition checkpoint must be loaded before it can be updated.");
        }

        var checkpointValue = long.Parse(checkpoint, NumberStyles.None, CultureInfo.InvariantCulture);
        var ownerEpoch = long.Parse(expectedVersion, NumberStyles.None, CultureInfo.InvariantCulture);
        var update = await queries.AdvanceStreamCheckpointAsync(
            serviceId,
            providerId,
            queueId,
            ownerEpoch,
            checkpointValue,
            cancellationToken);
        return ResolveCheckpointUpdate(
            $"{serviceId}/{providerId}/{queueId}",
            partition.OwnerEpoch,
            update);
    }

    internal static StreamCheckpointStoreState ResolveCheckpointUpdate(
        string partitionId,
        long acquiredOwnerEpoch,
        AdoNetStreamCheckpointUpdate? update)
    {
        if (update is not null && update.OwnerEpoch == acquiredOwnerEpoch)
        {
            return new(
                (update.Checkpoint ?? 0).ToString(CultureInfo.InvariantCulture),
                update.OwnerEpoch.ToString(CultureInfo.InvariantCulture));
        }

        throw new InvalidOperationException(
            $"ADO.NET stream partition ownership was lost for '{partitionId}' at epoch {acquiredOwnerEpoch}. The stale receiver cannot advance its checkpoint.");
    }

    public Task Initialize(
        RecoverableStreamStartPosition position,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_partition is null)
        {
            throw new InvalidOperationException("The ADO.NET stream partition checkpoint must be loaded before initializing its source.");
        }

        _readOffset = position.Checkpoint is null
            ? 0
            : long.Parse(position.Checkpoint, NumberStyles.None, CultureInfo.InvariantCulture);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<AdoNetStreamMessage>> Read(
        int maxCount,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _retentionFailure) is { } retentionFailure)
        {
            throw retentionFailure;
        }

        var messages = await queries.ReadStreamMessagesAsync(
            serviceId,
            providerId,
            queueId,
            _readOffset,
            Math.Min(maxCount, options.MaxMessagesPerRead),
            cancellationToken);

        var cleanup = await queries.CleanupStreamMessagesAsync(
            serviceId,
            providerId,
            queueId,
            _partition!.OwnerEpoch,
            AdoNetStreamTime.ToSqlSeconds(options.RetentionPeriod),
            options.MaximumRetentionPeriod is { } maximum
                ? AdoNetStreamTime.ToSqlSeconds(maximum)
                : null,
            AdoNetStreamTime.ToSqlSeconds(options.CleanupInterval),
            options.CleanupBatchSize,
            cancellationToken);
        if (cleanup.HardDeletedCount > 0)
        {
            LogHardRetentionCrossed(
                logger,
                serviceId,
                providerId,
                queueId,
                cleanup.HardDeletedCount,
                cleanup.HardDeletedFromMessageId,
                cleanup.HardDeletedThroughMessageId,
                cleanup.Checkpoint,
                cleanup.ActiveReplayWatermark);
        }

        var readThrough = messages.Count > 0 ? messages[^1].MessageId : _readOffset;
        if (cleanup.HardDeletedThroughMessageId is { } hardDeletedThrough
            && hardDeletedThrough > readThrough)
        {
            var failure = new DataNotAvailableException(
                $"ADO.NET stream partition '{serviceId}/{providerId}/{queueId}' lost unread retained records after "
                + $"message {readThrough}: hard retention deleted through message {hardDeletedThrough}.");
            var retainedFailure = Interlocked.CompareExchange(ref _retentionFailure, failure, comparand: null) ?? failure;
            throw retainedFailure;
        }

        _pendingHardDeletedThrough = cleanup.HardDeletedThroughMessageId is { } deletedThrough
            && deletedThrough > _readOffset
                ? deletedThrough
                : null;
        return messages as IReadOnlyList<AdoNetStreamMessage> ?? messages.ToList();
    }

    public void MessagesAdded(IReadOnlyList<AdoNetStreamMessage> messages)
    {
        if (messages.Count > 0)
        {
            _readOffset = messages[^1].MessageId;
        }

        if (_pendingHardDeletedThrough is { } hardDeletedThrough
            && hardDeletedThrough <= _readOffset)
        {
            _pendingHardDeletedThrough = null;
        }
    }

    public void MessagesAddFailed(IReadOnlyList<AdoNetStreamMessage> messages)
    {
        if (_pendingHardDeletedThrough is not { } hardDeletedThrough)
        {
            return;
        }

        var failure = new DataNotAvailableException(
            $"ADO.NET stream partition '{serviceId}/{providerId}/{queueId}' could not retain hard-deleted records "
            + $"through message {hardDeletedThrough} because cache admission failed before the read offset advanced.");
        Interlocked.CompareExchange(ref _retentionFailure, failure, comparand: null);
        _pendingHardDeletedThrough = null;
    }

    public Task Shutdown(CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested
            ? Task.FromCanceled(cancellationToken)
            : Task.CompletedTask;

    public async ValueTask<IRecoverableStreamReplaySource<AdoNetStreamMessage>> Create(
        StreamId streamId,
        StreamSequenceToken token,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeToken(token, out var adoNetToken))
        {
            throw new DataNotAvailableException(
                $"The replay token does not belong to ADO.NET stream partition '{serviceId}/{providerId}/{queueId}'.");
        }

        if (_partition is not { } partition)
        {
            throw new InvalidOperationException(
                "The ADO.NET stream partition must be acquired before a historical reader is created.");
        }

        var afterMessageId = adoNetToken.SequenceNumber > 0 ? adoNetToken.SequenceNumber - 1 : 0;
        var readerId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var leaseDurationSeconds = AdoNetStreamTime.ToSqlSeconds(options.ReplayLeaseDuration);
        AdoNetStreamReplayLeaseState lease;
        try
        {
            lease = await queries.AcquireStreamReplayLeaseAsync(
                serviceId,
                providerId,
                queueId,
                readerId,
                streamId.FullKey.ToArray(),
                streamId.Namespace.Length,
                partition.OwnerEpoch,
                afterMessageId,
                leaseDurationSeconds,
                cancellationToken);
        }
        catch (DbException exception)
        {
            throw new TransientStreamReplayException(
                $"ADO.NET replay lease admission temporarily failed for '{serviceId}/{providerId}/{queueId}'.",
                exception);
        }
        ThrowForReplayStatus(lease, readerId, afterMessageId);
        return new AdoNetReplaySource(
            serviceId,
            providerId,
            queueId,
            readerId,
            partition.OwnerEpoch,
            afterMessageId,
            leaseDurationSeconds,
            options.ReplayLeaseRenewalInterval,
            queries,
            _timeProvider);
    }

    private bool TryNormalizeToken(
        StreamSequenceToken token,
        out AdoNetStreamSequenceToken normalized)
    {
        if (token is AdoNetStreamSequenceToken adoNetToken
            && string.Equals(adoNetToken.ServiceId, serviceId, StringComparison.Ordinal)
            && string.Equals(adoNetToken.ProviderId, providerId, StringComparison.Ordinal)
            && string.Equals(adoNetToken.QueueId, queueId, StringComparison.Ordinal))
        {
            normalized = adoNetToken;
            return true;
        }

        if (token is PartitionedStreamSequenceToken partitionedToken
            && string.Equals(
                partitionedToken.ProviderIdentity,
                AdoNetStreamSequenceToken.GetProviderIdentity(serviceId, providerId),
                StringComparison.Ordinal)
            && string.Equals(partitionedToken.PartitionIdentity, queueId, StringComparison.Ordinal)
            && long.TryParse(
                partitionedToken.Position,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var sequenceNumber))
        {
            normalized = new(
                serviceId,
                providerId,
                queueId,
                sequenceNumber,
                partitionedToken.EventIndex);
            return true;
        }

        if (token.GetType() == typeof(EventSequenceTokenV2))
        {
            normalized = new(
                serviceId,
                providerId,
                queueId,
                token.SequenceNumber,
                token.EventIndex);
            return true;
        }

        normalized = null!;
        return false;
    }

    private void ThrowIfRetentionGap(AdoNetStreamPartitionState state)
    {
        if (HasRetentionGap(state))
        {
            throw new DataNotAvailableException(
                $"ADO.NET stream partition '{serviceId}/{providerId}/{queueId}' has a retention gap: "
                + $"checkpoint {state.Checkpoint}, earliest retained record {state.EarliestMessageId?.ToString(CultureInfo.InvariantCulture) ?? "<empty>"}, "
                + $"next message id {state.NextMessageId}, tail {state.TailMessageId?.ToString(CultureInfo.InvariantCulture) ?? "<empty>"}.");
        }
    }

    internal static bool HasRetentionGap(AdoNetStreamPartitionState state)
    {
        if (state.Checkpoint is not { } checkpoint)
        {
            return false;
        }

        var earliestAvailablePosition = state.EarliestMessageId ?? state.NextMessageId;
        return checkpoint < earliestAvailablePosition - 1;
    }

    private static void ThrowForReplayStatus(
        AdoNetStreamReplayLeaseState lease,
        string readerId,
        long requestedPosition)
    {
        switch (lease.Status)
        {
            case AdoNetStreamReplayStatus.Acquired:
            case AdoNetStreamReplayStatus.Active:
            case AdoNetStreamReplayStatus.Released:
                return;
            case AdoNetStreamReplayStatus.HistoryUnavailable:
                throw new DataNotAvailableException(
                    $"ADO.NET replay reader '{readerId}' requested partition position {requestedPosition}, "
                    + $"but the earliest retained record is {lease.EarliestMessageId?.ToString(CultureInfo.InvariantCulture) ?? "<empty>"}.");
            case AdoNetStreamReplayStatus.Expired:
                throw new DataNotAvailableException(
                    $"ADO.NET replay lease '{readerId}' expired while reading retained partition history.");
            case AdoNetStreamReplayStatus.OwnershipLost:
                throw new InvalidOperationException(
                    $"ADO.NET stream partition ownership was lost while operating replay lease '{readerId}'.");
            default:
                throw new InvalidOperationException(
                    $"ADO.NET replay lease '{readerId}' returned unsupported status '{lease.Status}'.");
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Hard stream retention deleted {DeletedCount} records for {ServiceId}/{ProviderId}/{QueueId} from {DeletedFrom} through {DeletedThrough}, crossing checkpoint {Checkpoint} or replay watermark {ReplayWatermark}.")]
    private static partial void LogHardRetentionCrossed(
        ILogger logger,
        string serviceId,
        string providerId,
        string queueId,
        int deletedCount,
        long? deletedFrom,
        long? deletedThrough,
        long? checkpoint,
        long? replayWatermark);

    private sealed class AdoNetReplaySource : IRecoverableStreamReplaySource<AdoNetStreamMessage>
    {
        private readonly string _serviceId;
        private readonly string _providerId;
        private readonly string _queueId;
        private readonly string _readerId;
        private readonly long _ownerEpoch;
        private readonly int _leaseDurationSeconds;
        private readonly TimeSpan _renewalInterval;
        private readonly RelationalOrleansQueries _queries;
        private readonly TimeProvider _timeProvider;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _heartbeatTask;
        private ExceptionDispatchInfo? _failure;
        private long _readOffset;
        private long _safeWatermark;
        private int _disposed;

        public AdoNetReplaySource(
            string serviceId,
            string providerId,
            string queueId,
            string readerId,
            long ownerEpoch,
            long readOffset,
            int leaseDurationSeconds,
            TimeSpan renewalInterval,
            RelationalOrleansQueries queries,
            TimeProvider timeProvider)
        {
            _serviceId = serviceId;
            _providerId = providerId;
            _queueId = queueId;
            _readerId = readerId;
            _ownerEpoch = ownerEpoch;
            _readOffset = readOffset;
            _safeWatermark = readOffset;
            _leaseDurationSeconds = leaseDurationSeconds;
            _renewalInterval = renewalInterval;
            _queries = queries;
            _timeProvider = timeProvider;
            _heartbeatTask = RunHeartbeat();
        }

        public async ValueTask<RecoverableStreamReplayReadResult<AdoNetStreamMessage>> Read(
            int maxCount,
            CancellationToken cancellationToken)
        {
            ThrowIfFailed();
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _cancellation.Token);
            AdoNetStreamReplayPage page;
            try
            {
                page = await _queries.ReadStreamReplayMessagesAsync(
                    _serviceId,
                    _providerId,
                    _queueId,
                    _readerId,
                    _ownerEpoch,
                    _readOffset,
                    maxCount,
                    _leaseDurationSeconds,
                    linkedCancellation.Token);
            }
            catch (DbException exception)
            {
                throw new TransientStreamReplayException(
                    $"ADO.NET replay reader '{_readerId}' temporarily failed.",
                    exception);
            }
            ThrowForReplayStatus(page.Lease, _readerId, _readOffset);
            var isAtTail = page.Lease.TailMessageId is not { } tail
                || page.Messages.Count == 0 && _readOffset >= tail
                || page.Messages.Count > 0 && page.Messages[^1].MessageId >= tail;
            return new(page.Messages, isAtTail);
        }

        public void MessagesAdded(IReadOnlyList<AdoNetStreamMessage> messages)
        {
            if (messages.Count > 0)
            {
                _readOffset = messages[^1].MessageId;
            }
        }

        public void UpdateProgress(StreamSequenceToken token)
        {
            if (token is not AdoNetStreamSequenceToken adoNetToken
                || !string.Equals(adoNetToken.ServiceId, _serviceId, StringComparison.Ordinal)
                || !string.Equals(adoNetToken.ProviderId, _providerId, StringComparison.Ordinal)
                || !string.Equals(adoNetToken.QueueId, _queueId, StringComparison.Ordinal))
            {
                throw new ArgumentOutOfRangeException(nameof(token));
            }

            var current = Volatile.Read(ref _safeWatermark);
            while (current < adoNetToken.SequenceNumber)
            {
                var previous = Interlocked.CompareExchange(
                    ref _safeWatermark,
                    adoNetToken.SequenceNumber,
                    current);
                if (previous == current)
                {
                    break;
                }

                current = previous;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await StopHeartbeat();
                var result = await _queries.ReleaseStreamReplayLeaseAsync(
                    _serviceId,
                    _providerId,
                    _queueId,
                    _readerId,
                    _ownerEpoch,
                    CancellationToken.None);
                ThrowForReplayStatus(result, _readerId, _safeWatermark);
            }
            finally
            {
                _cancellation.Dispose();
            }
        }

        public async ValueTask ShutdownAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await StopHeartbeat();
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                _cancellation.Dispose();
            }
        }

        private async Task RunHeartbeat()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(_renewalInterval, _timeProvider, _cancellation.Token);
                    var watermark = Volatile.Read(ref _safeWatermark);
                    AdoNetStreamReplayLeaseState result;
                    try
                    {
                        result = await _queries.UpdateStreamReplayLeaseAsync(
                            _serviceId,
                            _providerId,
                            _queueId,
                            _readerId,
                            _ownerEpoch,
                            watermark,
                            _leaseDurationSeconds,
                            _cancellation.Token);
                    }
                    catch (DbException exception)
                    {
                        throw new TransientStreamReplayException(
                            $"ADO.NET replay lease '{_readerId}' renewal temporarily failed.",
                            exception);
                    }
                    ThrowForReplayStatus(result, _readerId, watermark);
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _failure, ExceptionDispatchInfo.Capture(exception));
                _cancellation.Cancel();
            }
        }

        private async Task StopHeartbeat()
        {
            _cancellation.Cancel();
            await _heartbeatTask;
            ThrowIfFailed();
        }

        private void ThrowIfFailed() => Volatile.Read(ref _failure)?.Throw();
    }
}

internal sealed class AdoNetRecoverableStreamDataAdapter(
    string serviceId,
    string providerId,
    string queueId,
    Serializer<AdoNetBatchContainer> serializer) : IRecoverableStreamDataAdapter<AdoNetStreamMessage>
{
    public AdoNetRecoverableStreamDataAdapter(Serializer<AdoNetBatchContainer> serializer)
        : this(string.Empty, string.Empty, string.Empty, serializer)
    {
    }

    public StreamPosition GetStreamPosition(AdoNetStreamMessage queueMessage)
        => new(
            queueMessage.StreamId,
            new AdoNetStreamSequenceToken(serviceId, providerId, queueId, queueMessage.MessageId));

    public CachedMessage FromQueueMessage(
        StreamPosition streamPosition,
        AdoNetStreamMessage queueMessage,
        DateTime dequeueTimeUtc,
        Func<int, ArraySegment<byte>> getSegment)
    {
        var size = SegmentBuilder.CalculateAppendSize(queueMessage.Payload);
        var segment = getSegment(size);
        var offset = 0;
        SegmentBuilder.Append(segment, ref offset, queueMessage.Payload);
        return new CachedMessage
        {
            StreamId = streamPosition.StreamId,
            SequenceNumber = queueMessage.MessageId,
            EventIndex = streamPosition.SequenceToken.EventIndex,
            EnqueueTimeUtc = queueMessage.CreatedOn,
            DequeueTimeUtc = dequeueTimeUtc,
            Segment = segment,
        };
    }

    public IBatchContainer GetBatchContainer(ref CachedMessage cachedMessage)
    {
        var offset = 0;
        var payload = SegmentBuilder.ReadNextBytes(cachedMessage.Segment, ref offset).ToArray();
        var message = new AdoNetStreamMessage(
            serviceId,
            providerId,
            queueId,
            cachedMessage.SequenceNumber,
            cachedMessage.StreamId.FullKey.ToArray(),
            cachedMessage.StreamId.Namespace.Length,
            cachedMessage.EnqueueTimeUtc,
            payload);
        return AdoNetBatchContainer.FromMessage(serializer, message);
    }

    public StreamSequenceToken GetSequenceToken(ref CachedMessage cachedMessage)
        => new AdoNetStreamSequenceToken(
            serviceId,
            providerId,
            queueId,
            cachedMessage.SequenceNumber,
            cachedMessage.EventIndex);

    public int Compare(ref CachedMessage cachedMessage, StreamSequenceToken token)
    {
        if (!TryNormalizeToken(token, out var adoNetToken))
        {
            throw new ArgumentOutOfRangeException(nameof(token));
        }

        var difference = cachedMessage.SequenceNumber.CompareTo(adoNetToken.SequenceNumber);
        return difference != 0 ? difference : cachedMessage.EventIndex.CompareTo(adoNetToken.EventIndex);
    }

    public string GetOffset(ref CachedMessage cachedMessage)
        => cachedMessage.SequenceNumber.ToString(CultureInfo.InvariantCulture);

    public bool TryGetOffset(StreamSequenceToken token, out string offset)
    {
        if (TryNormalizeToken(token, out var adoNetToken))
        {
            offset = token.SequenceNumber.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        offset = string.Empty;
        return false;
    }

    public StreamSequenceToken GetRecordToken(StreamSequenceToken token)
        => TryNormalizeToken(token, out var adoNetToken)
            ? adoNetToken.CreateSequenceTokenForEvent(0)
            : throw new ArgumentOutOfRangeException(nameof(token));

    private bool TryNormalizeToken(
        StreamSequenceToken token,
        out AdoNetStreamSequenceToken normalized)
    {
        if (token is AdoNetStreamSequenceToken adoNetToken
            && string.Equals(adoNetToken.ServiceId, serviceId, StringComparison.Ordinal)
            && string.Equals(adoNetToken.ProviderId, providerId, StringComparison.Ordinal)
            && string.Equals(adoNetToken.QueueId, queueId, StringComparison.Ordinal))
        {
            normalized = adoNetToken;
            return true;
        }

        if (token is PartitionedStreamSequenceToken partitionedToken
            && string.Equals(
                partitionedToken.ProviderIdentity,
                AdoNetStreamSequenceToken.GetProviderIdentity(serviceId, providerId),
                StringComparison.Ordinal)
            && string.Equals(partitionedToken.PartitionIdentity, queueId, StringComparison.Ordinal)
            && long.TryParse(
                partitionedToken.Position,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var sequenceNumber))
        {
            normalized = new(
                serviceId,
                providerId,
                queueId,
                sequenceNumber,
                partitionedToken.EventIndex);
            return true;
        }

        if (token.GetType() == typeof(EventSequenceTokenV2))
        {
            normalized = new(
                serviceId,
                providerId,
                queueId,
                token.SequenceNumber,
                token.EventIndex);
            return true;
        }

        normalized = null!;
        return false;
    }
}

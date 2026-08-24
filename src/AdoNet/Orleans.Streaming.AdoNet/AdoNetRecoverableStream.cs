using System.Globalization;
using Orleans.Providers.Streams.Common;

namespace Orleans.Streaming.AdoNet;

internal sealed partial class AdoNetRecoverableStream(
    string serviceId,
    string providerId,
    string queueId,
    AdoNetStreamOptions options,
    RelationalOrleansQueries queries,
    ILogger logger) : IRecoverableStreamSource<AdoNetStreamMessage>, IStreamCheckpointStore
{
    private AdoNetStreamPartitionState? _partition;
    private long _readOffset;
    private Task<AdoNetStreamPartitionState>? _acquisitionTask;

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
                cleanup.Checkpoint);
        }

        return messages as IReadOnlyList<AdoNetStreamMessage> ?? messages.ToList();
    }

    public void MessagesAdded(IReadOnlyList<AdoNetStreamMessage> messages)
    {
        if (messages.Count > 0)
        {
            _readOffset = messages[^1].MessageId;
        }
    }

    public Task Shutdown(CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested
            ? Task.FromCanceled(cancellationToken)
            : Task.CompletedTask;

    private void ThrowIfRetentionGap(AdoNetStreamPartitionState state)
    {
        if (state.Checkpoint is { } checkpoint
            && state.EarliestMessageId is { } earliest
            && checkpoint < earliest - 1)
        {
            throw new DataNotAvailableException(
                $"ADO.NET stream partition '{serviceId}/{providerId}/{queueId}' has a retention gap: "
                + $"checkpoint {checkpoint}, earliest retained record {earliest}, tail {state.TailMessageId?.ToString(CultureInfo.InvariantCulture) ?? "<empty>"}.");
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Hard stream retention deleted {DeletedCount} records for {ServiceId}/{ProviderId}/{QueueId} from {DeletedFrom} through {DeletedThrough}, crossing checkpoint {Checkpoint}.")]
    private static partial void LogHardRetentionCrossed(
        ILogger logger,
        string serviceId,
        string providerId,
        string queueId,
        int deletedCount,
        long? deletedFrom,
        long? deletedThrough,
        long? checkpoint);
}

internal sealed class AdoNetRecoverableStreamDataAdapter(
    Serializer<AdoNetBatchContainer> serializer) : IRecoverableStreamDataAdapter<AdoNetStreamMessage>
{
    public StreamPosition GetStreamPosition(AdoNetStreamMessage queueMessage)
        => new(queueMessage.StreamId, new EventSequenceTokenV2(queueMessage.MessageId));

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
            string.Empty,
            string.Empty,
            string.Empty,
            cachedMessage.SequenceNumber,
            cachedMessage.StreamId.FullKey.ToArray(),
            cachedMessage.StreamId.Namespace.Length,
            cachedMessage.EnqueueTimeUtc,
            payload);
        return AdoNetBatchContainer.FromMessage(serializer, message);
    }

    public StreamSequenceToken GetSequenceToken(ref CachedMessage cachedMessage)
        => new EventSequenceTokenV2(cachedMessage.SequenceNumber, cachedMessage.EventIndex);

    public string GetOffset(ref CachedMessage cachedMessage)
        => cachedMessage.SequenceNumber.ToString(CultureInfo.InvariantCulture);

    public bool TryGetOffset(StreamSequenceToken token, out string offset)
    {
        offset = token.SequenceNumber.ToString(CultureInfo.InvariantCulture);
        return token is EventSequenceTokenV2;
    }
}

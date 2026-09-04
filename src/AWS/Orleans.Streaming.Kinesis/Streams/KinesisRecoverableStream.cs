using System.Net;
using Amazon.Kinesis;
using Amazon.Kinesis.Model;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Streaming.Kinesis;

internal sealed class KinesisCacheRecord(
    Record record,
    long sequenceNumber)
{
    private byte[]? _rawPayload;

    public Record Record { get; } = record;

    public long SequenceNumber { get; } = sequenceNumber;

    public KinesisBatchContainer.Body? Body { get; set; }

    public byte[] RawPayload => _rawPayload ??= Record.Data.ToArray();
}

internal sealed class KinesisRecoverableStreamSource(
    IAmazonKinesis client,
    string streamName,
    string partition,
    KinesisShardTopologyMonitor topologyMonitor,
    KinesisShardReadThrottle readThrottle)
    : IRecoverableStreamSource<KinesisCacheRecord>, IRecoverableStreamReplaySource<KinesisCacheRecord>
{
    private string? _shardIterator;
    private string? _readOffset;
    private long _nextSequenceNumber;
    private bool _shardExhausted;
    private bool _topologyCheckRequired;
    private bool _resetRequired;
    private bool _readOffsetInclusive;

    public async Task Initialize(
        RecoverableStreamStartPosition position,
        CancellationToken cancellationToken)
    {
        _readOffset = position.Checkpoint;
        _readOffsetInclusive = false;
        await ResetShardIterator(cancellationToken);
    }

    public async Task<IReadOnlyList<KinesisCacheRecord>> Read(
        int maxCount,
        CancellationToken cancellationToken)
        => (await ReadCore(maxCount, cancellationToken)).Messages;

    public async Task InitializeReplay(
        KinesisSequenceToken token,
        CancellationToken cancellationToken)
    {
        _readOffset = token.ShardSequence;
        _readOffsetInclusive = true;
        await ResetShardIterator(cancellationToken);
    }

    async ValueTask<RecoverableStreamReplayReadResult<KinesisCacheRecord>>
        IRecoverableStreamReplaySource<KinesisCacheRecord>.Read(
            int maxCount,
            CancellationToken cancellationToken)
    {
        try
        {
            return await ReadCore(maxCount, cancellationToken);
        }
        catch (Amazon.Kinesis.Model.InvalidArgumentException exception)
        {
            throw new DataNotAvailableException(
                $"Kinesis rejected shard sequence '{_readOffset}' for shard '{partition}'.",
                exception);
        }
    }

    private async Task<RecoverableStreamReplayReadResult<KinesisCacheRecord>> ReadCore(
        int maxCount,
        CancellationToken cancellationToken)
    {
        if (_resetRequired)
        {
            await ResetShardIterator(cancellationToken);
            _resetRequired = false;
        }

        if (!await topologyMonitor.CheckTopology(_topologyCheckRequired, cancellationToken))
        {
            return new([], isAtTail: false);
        }

        _topologyCheckRequired = false;
        if (_shardExhausted)
        {
            return new([], isAtTail: true);
        }

        var request = new GetRecordsRequest
        {
            Limit = maxCount,
            ShardIterator = _shardIterator,
        };

        GetRecordsResponse response;
        while (true)
        {
            await readThrottle.Wait(cancellationToken);
            request.ShardIterator = _shardIterator;
            try
            {
                response = await client.GetRecordsAsync(request, cancellationToken);
                break;
            }
            catch (ExpiredIteratorException)
            {
                await ResetShardIterator(cancellationToken);
                if (_shardExhausted)
                {
                    return new([], isAtTail: true);
                }
            }
            catch (ProvisionedThroughputExceededException)
            {
            }
            catch (AmazonKinesisException exception)
                when (exception.StatusCode == HttpStatusCode.TooManyRequests
                    || (int)exception.StatusCode >= 500)
            {
            }
        }

        _shardIterator = response.NextShardIterator;
        if (string.IsNullOrEmpty(_shardIterator))
        {
            _shardExhausted = true;
            _topologyCheckRequired = true;
        }

        if (response.Records is not { Count: > 0 } records)
        {
            return new([], _readOffsetInclusive ? false : response.MillisBehindLatest <= 0);
        }

        if (_readOffsetInclusive
            && KinesisSequenceToken.CompareShardSequences(
                records[0].SequenceNumber,
                _readOffset!) != 0)
        {
            throw new DataNotAvailableException(
                $"Kinesis returned shard sequence '{records[0].SequenceNumber}' after the requested retained position '{_readOffset}' in shard '{partition}'.");
        }

        var result = new List<KinesisCacheRecord>(records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            result.Add(new KinesisCacheRecord(records[i], _nextSequenceNumber + i));
        }

        return new(result, response.MillisBehindLatest <= 0);
    }

    public void MessagesAdded(IReadOnlyList<KinesisCacheRecord> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        _nextSequenceNumber += messages.Count;
        _readOffset = messages[^1].Record.SequenceNumber;
        _readOffsetInclusive = false;
    }

    public void MessagesAddFailed(IReadOnlyList<KinesisCacheRecord> messages)
    {
        _resetRequired = true;
    }

    public Task Shutdown(CancellationToken cancellationToken)
    {
        client.Dispose();
        return cancellationToken.IsCancellationRequested
            ? Task.FromCanceled(cancellationToken)
            : Task.CompletedTask;
    }

    private async Task ResetShardIterator(CancellationToken cancellationToken)
    {
        var request = new GetShardIteratorRequest
        {
            StreamName = streamName,
            ShardId = partition,
            ShardIteratorType = string.IsNullOrEmpty(_readOffset)
                ? ShardIteratorType.TRIM_HORIZON
                : _readOffsetInclusive
                    ? ShardIteratorType.AT_SEQUENCE_NUMBER
                    : ShardIteratorType.AFTER_SEQUENCE_NUMBER,
            StartingSequenceNumber = _readOffset,
        };
        var response = await client.GetShardIteratorAsync(request, cancellationToken);
        _shardIterator = response.ShardIterator;
        _shardExhausted = string.IsNullOrEmpty(_shardIterator);
    }

    public ValueTask DisposeAsync() => new(Shutdown(CancellationToken.None));
}

internal sealed class KinesisShardReadThrottle(
    TimeSpan interval,
    TimeProvider timeProvider)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTimeOffset _nextReadUtc;

    public async ValueTask Wait(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var delay = _nextReadUtc - timeProvider.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, timeProvider, cancellationToken);
            }

            _nextReadUtc = timeProvider.GetUtcNow() + interval;
        }
        finally
        {
            _lock.Release();
        }
    }
}

internal sealed class KinesisReplaySourceFactory(
    Func<IAmazonKinesis> clientFactory,
    string streamName,
    string partition,
    KinesisShardTopologyMonitor topologyMonitor,
    KinesisShardReadThrottle readThrottle)
    : IRecoverableStreamReplaySourceFactory<KinesisCacheRecord>
{
    public async ValueTask<IRecoverableStreamReplaySource<KinesisCacheRecord>> Create(
        StreamId streamId,
        StreamSequenceToken token,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeToken(token, out var kinesisToken))
        {
            throw new DataNotAvailableException(
                $"The replay token does not belong to Kinesis shard '{partition}'.");
        }

        var source = new KinesisRecoverableStreamSource(
            clientFactory(),
            streamName,
            partition,
            topologyMonitor,
            readThrottle);
        try
        {
            await source.InitializeReplay(kinesisToken, cancellationToken);
            return source;
        }
        catch (Amazon.Kinesis.Model.InvalidArgumentException exception)
        {
            await source.DisposeAsync();
            throw new DataNotAvailableException(
                $"Kinesis rejected shard sequence '{kinesisToken.ShardSequence}' for shard '{partition}'.",
                exception);
        }
        catch
        {
            await source.DisposeAsync();
            throw;
        }
    }

    private bool TryNormalizeToken(
        StreamSequenceToken token,
        out KinesisSequenceToken normalized)
    {
        if (token is KinesisSequenceToken kinesisToken
            && string.Equals(kinesisToken.StreamName, streamName, StringComparison.Ordinal)
            && string.Equals(kinesisToken.ShardId, partition, StringComparison.Ordinal))
        {
            normalized = kinesisToken;
            return true;
        }

        if (token is PartitionedStreamSequenceToken partitionedToken
            && string.Equals(partitionedToken.ProviderIdentity, streamName, StringComparison.Ordinal)
            && string.Equals(partitionedToken.PartitionIdentity, partition, StringComparison.Ordinal))
        {
            normalized = new(
                streamName,
                partition,
                partitionedToken.Position,
                partitionedToken.SequenceNumber,
                partitionedToken.EventIndex);
            return true;
        }

        if (token is KinesisSequenceToken { StreamName: null, ShardId: null } legacyToken)
        {
            normalized = new(
                streamName,
                partition,
                legacyToken.ShardSequence,
                legacyToken.SequenceNumber,
                legacyToken.EventIndex);
            return true;
        }

        normalized = null!;
        return false;
    }
}

internal sealed class KinesisRecoverableStreamDataAdapter(
    string streamName,
    string partition,
    Serializer<KinesisBatchContainer.Body> serializer) : IRecoverableStreamDataAdapter<KinesisCacheRecord>
{
    public KinesisRecoverableStreamDataAdapter(Serializer<KinesisBatchContainer.Body> serializer)
        : this(string.Empty, string.Empty, serializer)
    {
    }

    public StreamPosition GetStreamPosition(KinesisCacheRecord queueMessage)
    {
        queueMessage.Body ??= serializer.Deserialize(queueMessage.RawPayload)!;
        return new(
            queueMessage.Body.StreamId,
            new KinesisSequenceToken(
                streamName,
                partition,
                queueMessage.Record.SequenceNumber,
                queueMessage.SequenceNumber,
                0));
    }

    public CachedMessage FromQueueMessage(
        StreamPosition streamPosition,
        KinesisCacheRecord queueMessage,
        DateTime dequeueTimeUtc,
        Func<int, ArraySegment<byte>> getSegment)
    {
        var payload = queueMessage.RawPayload;
        var size = SegmentBuilder.CalculateAppendSize(queueMessage.Record.SequenceNumber)
            + SegmentBuilder.CalculateAppendSize(payload);
        var segment = getSegment(size);
        var offset = 0;
        SegmentBuilder.Append(segment, ref offset, queueMessage.Record.SequenceNumber);
        SegmentBuilder.Append(segment, ref offset, payload);
        return new CachedMessage
        {
            StreamId = streamPosition.StreamId,
            SequenceNumber = queueMessage.SequenceNumber,
            EventIndex = streamPosition.SequenceToken.EventIndex,
            EnqueueTimeUtc = queueMessage.Record.ApproximateArrivalTimestamp?.ToUniversalTime() ?? dequeueTimeUtc,
            DequeueTimeUtc = dequeueTimeUtc,
            Segment = segment,
        };
    }

    public IBatchContainer GetBatchContainer(ref CachedMessage cachedMessage)
    {
        var offset = 0;
        var shardSequence = SegmentBuilder.ReadNextString(cachedMessage.Segment, ref offset)!;
        var payload = SegmentBuilder.ReadNextBytes(cachedMessage.Segment, ref offset).ToArray();
        return KinesisBatchContainer.FromCachedRecord(
            serializer,
            cachedMessage.StreamId,
            payload,
            streamName,
            partition,
            shardSequence,
            cachedMessage.SequenceNumber);
    }

    public StreamSequenceToken GetSequenceToken(ref CachedMessage cachedMessage)
    {
        var offset = 0;
        var shardSequence = SegmentBuilder.ReadNextString(cachedMessage.Segment, ref offset)!;
        return new KinesisSequenceToken(
            streamName,
            partition,
            shardSequence,
            cachedMessage.SequenceNumber,
            cachedMessage.EventIndex);
    }

    public int Compare(ref CachedMessage cachedMessage, StreamSequenceToken token)
    {
        if (token is not KinesisSequenceToken kinesisToken)
        {
            throw new ArgumentOutOfRangeException(nameof(token));
        }

        if (!IsPartitionMatch(kinesisToken))
        {
            throw new ArgumentOutOfRangeException(nameof(token));
        }

        var offset = 0;
        var shardSequence = SegmentBuilder.ReadNextString(cachedMessage.Segment, ref offset)!;
        var difference = KinesisSequenceToken.CompareShardSequences(shardSequence, kinesisToken.ShardSequence);
        return difference != 0 ? difference : cachedMessage.EventIndex.CompareTo(kinesisToken.EventIndex);
    }

    public string GetOffset(ref CachedMessage cachedMessage)
    {
        var offset = 0;
        return SegmentBuilder.ReadNextString(cachedMessage.Segment, ref offset)!;
    }

    public bool TryGetOffset(StreamSequenceToken token, out string offset)
    {
        if (token is KinesisSequenceToken kinesisToken && IsPartitionMatch(kinesisToken))
        {
            offset = kinesisToken.ShardSequence;
            return true;
        }

        offset = string.Empty;
        return false;
    }

    public StreamSequenceToken GetRecordToken(StreamSequenceToken token)
        => TryNormalizeToken(token, out var normalized)
            ? normalized.CreateSequenceTokenForEvent(0)
            : throw new ArgumentOutOfRangeException(nameof(token));

    private bool IsPartitionMatch(KinesisSequenceToken token)
        => string.IsNullOrEmpty(partition)
            ? string.IsNullOrEmpty(token.StreamName) && string.IsNullOrEmpty(token.ShardId)
            : (string.Equals(token.StreamName, streamName, StringComparison.Ordinal)
                && string.Equals(token.ShardId, partition, StringComparison.Ordinal))
                || (token.StreamName is null && token.ShardId is null);

    private bool TryNormalizeToken(
        StreamSequenceToken token,
        out KinesisSequenceToken normalized)
    {
        if (token is KinesisSequenceToken kinesisToken && IsPartitionMatch(kinesisToken))
        {
            normalized = kinesisToken.StreamName is null
                ? new(
                    streamName,
                    partition,
                    kinesisToken.ShardSequence,
                    kinesisToken.SequenceNumber,
                    kinesisToken.EventIndex)
                : kinesisToken;
            return true;
        }

        if (token is PartitionedStreamSequenceToken partitionedToken
            && string.Equals(partitionedToken.ProviderIdentity, streamName, StringComparison.Ordinal)
            && string.Equals(partitionedToken.PartitionIdentity, partition, StringComparison.Ordinal))
        {
            normalized = new(
                streamName,
                partition,
                partitionedToken.Position,
                partitionedToken.SequenceNumber,
                partitionedToken.EventIndex);
            return true;
        }

        normalized = null!;
        return false;
    }
}

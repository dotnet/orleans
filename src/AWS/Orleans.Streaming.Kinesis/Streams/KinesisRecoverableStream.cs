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
    public Record Record { get; } = record;

    public long SequenceNumber { get; } = sequenceNumber;

    public KinesisBatchContainer.Body? Body { get; set; }
}

internal sealed class KinesisRecoverableStreamSource(
    IAmazonKinesis client,
    string streamName,
    string partition,
    KinesisShardTopologyMonitor topologyMonitor,
    TimeSpan getRecordsInterval,
    TimeProvider timeProvider) : IRecoverableStreamSource<KinesisCacheRecord>
{
    private string? _shardIterator;
    private string? _readOffset;
    private long _nextSequenceNumber;
    private DateTimeOffset _nextGetRecordsUtc;
    private bool _shardExhausted;
    private bool _topologyCheckRequired;
    private bool _resetRequired;

    public async Task Initialize(
        RecoverableStreamStartPosition position,
        CancellationToken cancellationToken)
    {
        _readOffset = position.Checkpoint;
        await ResetShardIterator(cancellationToken);
    }

    public async Task<IReadOnlyList<KinesisCacheRecord>> Read(
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
            return [];
        }

        _topologyCheckRequired = false;
        if (_shardExhausted)
        {
            return [];
        }

        await WaitForGetRecordsInterval(cancellationToken);
        var request = new GetRecordsRequest
        {
            Limit = maxCount,
            ShardIterator = _shardIterator,
        };

        GetRecordsResponse response;
        try
        {
            response = await client.GetRecordsAsync(request, cancellationToken);
        }
        catch (ExpiredIteratorException)
        {
            await ResetShardIterator(cancellationToken);
            if (_shardExhausted)
            {
                return [];
            }

            await WaitForGetRecordsInterval(cancellationToken);
            request.ShardIterator = _shardIterator;
            response = await client.GetRecordsAsync(request, cancellationToken);
        }

        _shardIterator = response.NextShardIterator;
        if (string.IsNullOrEmpty(_shardIterator))
        {
            _shardExhausted = true;
            _topologyCheckRequired = true;
        }

        if (response.Records is not { Count: > 0 } records)
        {
            return [];
        }

        var result = new List<KinesisCacheRecord>(records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            result.Add(new KinesisCacheRecord(records[i], _nextSequenceNumber + i));
        }

        return result;
    }

    public void MessagesAdded(IReadOnlyList<KinesisCacheRecord> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        _nextSequenceNumber += messages.Count;
        _readOffset = messages[^1].Record.SequenceNumber;
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
                : ShardIteratorType.AFTER_SEQUENCE_NUMBER,
            StartingSequenceNumber = _readOffset,
        };
        var response = await client.GetShardIteratorAsync(request, cancellationToken);
        _shardIterator = response.ShardIterator;
        _shardExhausted = string.IsNullOrEmpty(_shardIterator);
    }

    private async Task WaitForGetRecordsInterval(CancellationToken cancellationToken)
    {
        var delay = _nextGetRecordsUtc - timeProvider.GetUtcNow();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, timeProvider, cancellationToken);
        }

        _nextGetRecordsUtc = timeProvider.GetUtcNow() + getRecordsInterval;
    }
}

internal sealed class KinesisRecoverableStreamDataAdapter(
    Serializer<KinesisBatchContainer.Body> serializer) : IRecoverableStreamDataAdapter<KinesisCacheRecord>
{
    public StreamPosition GetStreamPosition(KinesisCacheRecord queueMessage)
    {
        queueMessage.Body ??= serializer.Deserialize(queueMessage.Record.Data.ToArray())!;
        return new(
            queueMessage.Body.StreamId,
            new KinesisSequenceToken(
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
        var payload = queueMessage.Record.Data.ToArray();
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
            shardSequence,
            cachedMessage.SequenceNumber);
    }

    public StreamSequenceToken GetSequenceToken(ref CachedMessage cachedMessage)
    {
        var offset = 0;
        var shardSequence = SegmentBuilder.ReadNextString(cachedMessage.Segment, ref offset)!;
        return new KinesisSequenceToken(shardSequence, cachedMessage.SequenceNumber, cachedMessage.EventIndex);
    }

    public int Compare(ref CachedMessage cachedMessage, StreamSequenceToken token)
    {
        if (token is not KinesisSequenceToken kinesisToken)
        {
            throw new ArgumentOutOfRangeException(nameof(token));
        }

        var offset = 0;
        var shardSequence = SegmentBuilder.ReadNextString(cachedMessage.Segment, ref offset)!;
        var difference = CompareShardSequences(shardSequence, kinesisToken.ShardSequence);
        return difference != 0 ? difference : cachedMessage.EventIndex.CompareTo(kinesisToken.EventIndex);
    }

    private static int CompareShardSequences(string left, string right)
    {
        var leftStart = 0;
        while (leftStart < left.Length && left[leftStart] == '0')
        {
            leftStart++;
        }

        var rightStart = 0;
        while (rightStart < right.Length && right[rightStart] == '0')
        {
            rightStart++;
        }

        var lengthComparison = (left.Length - leftStart).CompareTo(right.Length - rightStart);
        return lengthComparison != 0
            ? lengthComparison
            : left.AsSpan(leftStart).SequenceCompareTo(right.AsSpan(rightStart));
    }

    public string GetOffset(ref CachedMessage cachedMessage)
    {
        var offset = 0;
        return SegmentBuilder.ReadNextString(cachedMessage.Segment, ref offset)!;
    }

    public bool TryGetOffset(StreamSequenceToken token, out string offset)
    {
        if (token is KinesisSequenceToken kinesisToken)
        {
            offset = kinesisToken.ShardSequence;
            return true;
        }

        offset = string.Empty;
        return false;
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
public class SimpleQueueCacheTests
{
    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void EarliestAvailableStartsAtOldestRetainedMessageForStream()
    {
        var cache = new SimpleQueueCache(10, NullLogger.Instance);
        var targetStream = StreamId.Create("namespace", Guid.NewGuid());
        var otherStream = StreamId.Create("namespace", Guid.NewGuid());
        cache.AddToCache(
        [
            new TestBatchContainer(targetStream, 1),
            new TestBatchContainer(otherStream, 2),
            new TestBatchContainer(targetStream, 3),
        ]);

        var result = ((IQueueCache)cache).TryGetCacheCursorAtPosition(
            targetStream,
            StreamSubscriptionStartPosition.EarliestAvailable);
        Assert.Equal(QueueCacheCursorResultKind.Success, result.Kind);
        Assert.NotNull(result.Cursor);
        var cursor = result.Cursor;

        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cursor.MoveNextWithResult().Kind);
        Assert.Equal(1, cursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cursor.MoveNextWithResult().Kind);
        Assert.Equal(3, cursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void EarliestAvailableWaitsForFirstFutureMatchingMessage()
    {
        var cache = new SimpleQueueCache(10, NullLogger.Instance);
        var targetStream = StreamId.Create("namespace", Guid.NewGuid());
        var otherStream = StreamId.Create("namespace", Guid.NewGuid());
        cache.AddToCache([new TestBatchContainer(otherStream, 100)]);
        var result = ((IQueueCache)cache).TryGetCacheCursorAtPosition(
            targetStream,
            StreamSubscriptionStartPosition.EarliestAvailable);
        Assert.Equal(QueueCacheCursorResultKind.Success, result.Kind);
        Assert.NotNull(result.Cursor);
        var cursor = result.Cursor;

        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);

        var unrelatedToken = new EventSequenceTokenV2(101);
        cache.AddToCache([new TestBatchContainer(otherStream, unrelatedToken)]);
        cursor.Refresh(unrelatedToken);
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);

        var targetToken = new EventSequenceTokenV2(1);
        cache.AddToCache([new TestBatchContainer(targetStream, targetToken)]);
        cursor.Refresh(targetToken);

        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cursor.MoveNextWithResult().Kind);
        Assert.Equal(targetToken, cursor.GetCurrent(out _)!.SequenceToken);
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void LatestStartsAfterNewestRetainedMessage()
    {
        var cache = new SimpleQueueCache(10, NullLogger.Instance);
        var stream = StreamId.Create("namespace", Guid.NewGuid());
        cache.AddToCache([new TestBatchContainer(stream, 1)]);

        var result = ((IQueueCache)cache).TryGetCacheCursorAtPosition(
            stream,
            StreamSubscriptionStartPosition.Latest);
        Assert.Equal(QueueCacheCursorResultKind.Success, result.Kind);
        Assert.NotNull(result.Cursor);
        var cursor = result.Cursor;

        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);

        var futureToken = new EventSequenceTokenV2(2);
        cache.AddToCache([new TestBatchContainer(stream, futureToken)]);
        cursor.Refresh(futureToken);

        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cursor.MoveNextWithResult().Kind);
        Assert.Equal(futureToken, cursor.GetCurrent(out _)!.SequenceToken);
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void LegacyLatestStartsAfterNewestRetainedMessage()
    {
        var cache = new SimpleQueueCache(10, NullLogger.Instance);
        var stream = StreamId.Create("namespace", Guid.NewGuid());
        cache.AddToCache([new TestBatchContainer(stream, 1)]);

#pragma warning disable CS0618 // Verify compatibility of the obsolete wrapper.
        var cursor = ((IQueueCache)cache).GetCacheCursorAtPosition(
            stream,
            StreamSubscriptionStartPosition.Latest);
#pragma warning restore CS0618

        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);
        var futureToken = new EventSequenceTokenV2(2);
        cache.AddToCache([new TestBatchContainer(stream, futureToken)]);
        cursor.Refresh(futureToken);
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cursor.MoveNextWithResult().Kind);
        Assert.Equal(futureToken, cursor.GetCurrent(out _)!.SequenceToken);
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void TryGetCacheCursorPreservesDerivedCursorOverride()
    {
        var cache = new DerivedSimpleQueueCache();

        var result = ((IQueueCache)cache).TryGetCacheCursor(default, null);

        Assert.Equal(QueueCacheCursorResultKind.Success, result.Kind);
        Assert.True(cache.GetCacheCursorCalled);
        Assert.Same(cache.Cursor, result.Cursor);
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void TryGetCacheCursorPreservesCacheMissDetails()
    {
        var cache = new SimpleQueueCache(10, NullLogger.Instance);
        var stream = StreamId.Create("namespace", Guid.NewGuid());
        var retainedToken = new EventSequenceTokenV2(2);
        var requestedToken = new EventSequenceTokenV2(1);
        cache.AddToCache([new TestBatchContainer(stream, retainedToken)]);

        var result = ((IQueueCache)cache).TryGetCacheCursor(stream, requestedToken);

        Assert.Equal(QueueCacheCursorResultKind.CacheMiss, result.Kind);
        Assert.Null(result.Cursor);
        var cacheMiss = Assert.NotNull(result.CacheMiss);
        Assert.Same(requestedToken, cacheMiss.RequestedToken);
        Assert.Same(retainedToken, cacheMiss.LowToken);
        Assert.Same(retainedToken, cacheMiss.HighToken);
        var exception = cacheMiss.ToException();
        Assert.Equal(cacheMiss.Requested, exception.Requested);
        Assert.Equal(cacheMiss.Low, exception.Low);
        Assert.Equal(cacheMiss.High, exception.High);
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void CursorAcquisitionPreservesCacheMissWhenCacheMutatesAfterPreflight()
    {
        var (cache, stream, requestedToken, retainedToken, retainedCursor) = CreateCacheWithMutationDuringInitialization();
        using (retainedCursor)
        {
            var result = ((IQueueCache)cache).TryGetCacheCursor(stream, requestedToken);

            Assert.Equal(QueueCacheCursorResultKind.CacheMiss, result.Kind);
            Assert.Null(result.Cursor);
            var cacheMiss = Assert.NotNull(result.CacheMiss);
            Assert.Same(requestedToken, cacheMiss.RequestedToken);
            Assert.Equal(2, cacheMiss.LowToken!.SequenceNumber);
            Assert.Equal(2, cacheMiss.HighToken!.SequenceNumber);
        }

        (cache, stream, requestedToken, retainedToken, retainedCursor) = CreateCacheWithMutationDuringInitialization();
        using (retainedCursor)
        {
#pragma warning disable CS0618 // Verify compatibility of the obsolete wrapper.
            var exception = Assert.Throws<QueueCacheMissException>(() => cache.GetCacheCursor(stream, requestedToken));
#pragma warning restore CS0618

            Assert.Equal(requestedToken.ToString(), exception.Requested);
            Assert.Equal(retainedToken.ToString(), exception.Low);
            Assert.Equal(retainedToken.ToString(), exception.High);
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void TryGetCacheCursorAtPositionDoesNotBypassDerivedCursorOverride()
    {
        var cache = new DerivedSimpleQueueCache();

        var result = ((IQueueCache)cache).TryGetCacheCursorAtPosition(
            default,
            StreamSubscriptionStartPosition.EarliestAvailable);

        Assert.Equal(QueueCacheCursorResultKind.NotSupported, result.Kind);
        Assert.False(cache.GetCacheCursorCalled);
        Assert.Null(result.Cursor);
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void GetCacheCursorAtPositionPreservesLegacyResultBehavior()
    {
        IQueueCache cache = new CacheMissPositionQueueCache();

#pragma warning disable CS0618 // Verify compatibility of the obsolete wrapper.
        var exception = Assert.Throws<QueueCacheMissException>(
            () => cache.GetCacheCursorAtPosition(default, StreamSubscriptionStartPosition.EarliestAvailable));
#pragma warning restore CS0618

        Assert.Equal("requested", exception.Requested);
        Assert.Equal("low", exception.Low);
        Assert.Equal("high", exception.High);
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void LatestLegacyPositionPreservesProviderException()
    {
        var innerException = new InvalidOperationException("inner");
        var expected = new QueueCacheMissException("provider message", innerException);
        IQueueCache cache = new LegacyThrowingQueueCache(expected);

#pragma warning disable CS0618 // Verify compatibility of the obsolete wrapper.
        var actual = Assert.Throws<QueueCacheMissException>(
            () => cache.GetCacheCursorAtPosition(default, StreamSubscriptionStartPosition.Latest));
#pragma warning restore CS0618

        Assert.Same(expected, actual);
        Assert.Same(innerException, actual.InnerException);
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void LatestRejectsInvalidDerivedCursorMoveResult()
    {
        var cursor = new InvalidMoveResultCursor();
        var cache = new DerivedSimpleQueueCache(cursor);

        Assert.Throws<InvalidOperationException>(
            () => ((IQueueCache)cache).TryGetCacheCursorAtPosition(
                default,
                StreamSubscriptionStartPosition.Latest));
        Assert.True(cursor.IsDisposed);
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void LatestDisposesDerivedCursorWhenMoveThrows()
    {
        var cursor = new ThrowingMoveResultCursor();
        var cache = new DerivedSimpleQueueCache(cursor);

        Assert.Throws<InvalidOperationException>(
            () => ((IQueueCache)cache).TryGetCacheCursorAtPosition(
                default,
                StreamSubscriptionStartPosition.Latest));
        Assert.True(cursor.IsDisposed);
    }

    private sealed class DerivedSimpleQueueCache : SimpleQueueCache
    {
        public DerivedSimpleQueueCache(IQueueCacheCursor? cursor = null) : base(10, NullLogger.Instance)
        {
            Cursor = cursor ?? new EmptyCursor();
        }

        public IQueueCacheCursor Cursor { get; }
        public bool GetCacheCursorCalled { get; private set; }

        [Obsolete("Use IQueueCache.TryGetCacheCursor instead.")]
        public override IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
        {
            GetCacheCursorCalled = true;
            return Cursor;
        }
    }

    private sealed class CacheMissPositionQueueCache : IQueueCache
    {
        public void AddToCache(IList<IBatchContainer> messages)
        {
        }

        public int GetMaxAddCount() => 1;

        public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
            => new EmptyCursor();

        public QueueCacheCursorResult<IQueueCacheCursor> TryGetCacheCursorAtPosition(
            StreamId streamId,
            StreamSubscriptionStartPosition startPosition)
            => QueueCacheCursorResult<IQueueCacheCursor>.FromCacheMiss(new("requested", "low", "high"));

        public bool IsUnderPressure() => false;

        public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
        {
            purgedItems = null!;
            return false;
        }
    }

    private sealed class LegacyThrowingQueueCache(QueueCacheMissException exception) : IQueueCache
    {
        public void AddToCache(IList<IBatchContainer> messages)
        {
        }

        public int GetMaxAddCount() => 1;

        public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token) => throw exception;

        public bool IsUnderPressure() => false;

        public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
        {
            purgedItems = null!;
            return false;
        }
    }

    private static (
        SimpleQueueCache Cache,
        StreamId Stream,
        StreamSequenceToken RequestedToken,
        StreamSequenceToken RetainedToken,
        IQueueCacheCursor RetainedCursor)
        CreateCacheWithMutationDuringInitialization()
    {
        var cache = new SimpleQueueCache(10, NullLogger.Instance);
        var stream = StreamId.Create("namespace", Guid.NewGuid());
        var retainedToken = new EventSequenceTokenV2(2);
        cache.AddToCache(
        [
            new TestBatchContainer(stream, 1),
            new TestBatchContainer(stream, retainedToken),
        ]);
        var retainedCursorResult = ((IQueueCache)cache).TryGetCacheCursor(stream, retainedToken);
        Assert.NotNull(retainedCursorResult.Cursor);
        var retainedCursor = retainedCursorResult.Cursor;
        var requestedToken = new MutatingCompareToken(
            1,
            2,
            () =>
            {
                Assert.True(cache.TryPurgeFromCache(out var purgedItems));
                Assert.Single(purgedItems);
            });
        return (cache, stream, requestedToken, retainedToken, retainedCursor);
    }

    private sealed class MutatingCompareToken : StreamSequenceToken
    {
        private readonly long _sequenceNumber;
        private readonly int _mutationComparison;
        private readonly Action _mutation;
        private int _comparisons;

        public MutatingCompareToken(long sequenceNumber, int mutationComparison, Action mutation)
        {
            _sequenceNumber = sequenceNumber;
            _mutationComparison = mutationComparison;
            _mutation = mutation;
        }

        public override long SequenceNumber
        {
            get => _sequenceNumber;
            protected set => throw new NotSupportedException();
        }

        public override int EventIndex
        {
            get => 0;
            protected set => throw new NotSupportedException();
        }

        public override int CompareTo(StreamSequenceToken? other)
        {
            if (Interlocked.Increment(ref _comparisons) == _mutationComparison)
            {
                _mutation();
            }

            return other is null
                ? 1
                : _sequenceNumber != other.SequenceNumber
                    ? _sequenceNumber.CompareTo(other.SequenceNumber)
                    : EventIndex.CompareTo(other.EventIndex);
        }

        public override bool Equals(StreamSequenceToken? other)
            => other is not null
                && _sequenceNumber == other.SequenceNumber
                && EventIndex == other.EventIndex;
    }

    private sealed class InvalidMoveResultCursor : EmptyCursor
    {
        public bool IsDisposed { get; private set; }

        public override void Dispose() => IsDisposed = true;

        public override QueueCacheCursorMoveResult MoveNextWithResult() => default;
    }

    private sealed class ThrowingMoveResultCursor : EmptyCursor
    {
        public bool IsDisposed { get; private set; }

        public override void Dispose() => IsDisposed = true;

        public override QueueCacheCursorMoveResult MoveNextWithResult()
            => throw new InvalidOperationException("Move failed.");
    }

    private class EmptyCursor : IQueueCacheCursor
    {
        public virtual void Dispose()
        {
        }

        public IBatchContainer? GetCurrent(out Exception? exception)
        {
            exception = null;
            return null;
        }

        public virtual bool MoveNext() => false;

        public virtual QueueCacheCursorMoveResult MoveNextWithResult()
            => QueueCacheCursorMoveResult.NoData;

        public void Refresh(StreamSequenceToken token)
        {
        }

        public void RecordDeliveryFailure()
        {
        }
    }

    private sealed class TestBatchContainer : IBatchContainer
    {
        public TestBatchContainer(StreamId streamId, long sequenceNumber)
            : this(streamId, new EventSequenceTokenV2(sequenceNumber))
        {
        }

        public TestBatchContainer(StreamId streamId, StreamSequenceToken token)
        {
            StreamId = streamId;
            SequenceToken = token;
        }

        public StreamId StreamId { get; }
        public StreamSequenceToken SequenceToken { get; }
        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => [];
        public bool ImportRequestContext() => false;
    }
}

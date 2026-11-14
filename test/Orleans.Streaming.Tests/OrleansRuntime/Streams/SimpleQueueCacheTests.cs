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
    public void LegacyNullTokenStartsAtNewestRetainedMessage()
    {
        var cache = new SimpleQueueCache(10, NullLogger.Instance);
        var stream = StreamId.Create("namespace", Guid.NewGuid());
        cache.AddToCache([new TestBatchContainer(stream, 1)]);

#pragma warning disable CS0618 // Verify compatibility of the obsolete wrapper.
        using var cursor = ((IQueueCache)cache).GetCacheCursor(stream, null);
#pragma warning restore CS0618

        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cursor.MoveNextWithResult().Kind);
        Assert.Equal(1, cursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);
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
    public void LegacyCursorDefaultAdapterPreservesDerivedSimpleQueueCacheCursorOverrides()
    {
        var cache = new DerivedCursorSimpleQueueCache();
        var stream = StreamId.Create("namespace", Guid.NewGuid());

        var acquisition = ((IQueueCache)cache).TryGetCacheCursor(stream, null);

        Assert.Equal(QueueCacheCursorResultKind.Success, acquisition.Kind);
        var cursor = Assert.IsType<DerivedSimpleQueueCacheCursor>(acquisition.Cursor);
        Assert.Same(cache.Cursor, cursor);
        Assert.Equal(1, cache.GetCacheCursorCount);
        try
        {
            IQueueCacheCursor interfaceCursor = cursor;
            Assert.Equal(QueueCacheCursorMoveResultKind.NoData, interfaceCursor.MoveNextWithResult().Kind);

            var first = new TestBatchContainer(stream, 1);
            cache.AddToCache([first]);
            interfaceCursor.Refresh(first.SequenceToken);
            Assert.Equal(QueueCacheCursorMoveResultKind.Success, interfaceCursor.MoveNextWithResult().Kind);
            Assert.Same(first, interfaceCursor.GetCurrent(out var firstException));
            Assert.Null(firstException);
            Assert.Equal(QueueCacheCursorMoveResultKind.NoData, interfaceCursor.MoveNextWithResult().Kind);

            var second = new TestBatchContainer(stream, 2);
            cache.AddToCache([second]);
            interfaceCursor.Refresh(second.SequenceToken);
            Assert.Equal(QueueCacheCursorMoveResultKind.Success, interfaceCursor.MoveNextWithResult().Kind);
            Assert.Same(second, interfaceCursor.GetCurrent(out var secondException));
            Assert.Null(secondException);

            var requested = new EventSequenceTokenV2(0);
            var low = new EventSequenceTokenV2(1);
            var high = new EventSequenceTokenV2(2);
            cursor.NextMoveException = new QueueCacheMissException(requested, low, high);
            var cacheMissResult = interfaceCursor.MoveNextWithResult();
            Assert.Equal(QueueCacheCursorMoveResultKind.CacheMiss, cacheMissResult.Kind);
            var cacheMiss = Assert.NotNull(cacheMissResult.CacheMiss);
            Assert.Equal(requested.ToString(), cacheMiss.Requested);
            Assert.Equal(low.ToString(), cacheMiss.Low);
            Assert.Equal(high.ToString(), cacheMiss.High);
            var legacyException = cacheMiss.ToException();
            Assert.Equal(requested.ToString(), legacyException.Requested);
            Assert.Equal(low.ToString(), legacyException.Low);
            Assert.Equal(high.ToString(), legacyException.High);

            var unexpected = new InvalidOperationException("Provider failure");
            cursor.NextMoveException = unexpected;
            Assert.Same(
                unexpected,
                Assert.Throws<InvalidOperationException>(() => interfaceCursor.MoveNextWithResult()));

            Assert.Equal(6, cursor.MoveNextCount);
            Assert.Equal(2, cursor.GetCurrentCount);
            Assert.Equal(0, cursor.DisposeCount);
        }
        finally
        {
            cursor.Dispose();
        }

        Assert.Equal(1, cursor.DisposeCount);
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
    public void CursorAcquisitionDisposesCursorAndPreservesRacedCacheMiss()
    {
        var (trackingCache, cache, stream, requestedToken, retainedToken, retainedCursor) = CreateCacheWithMutationDuringInitialization();
        using (retainedCursor)
        {
            var result = ((IQueueCache)cache).TryGetCacheCursor(stream, requestedToken);

            Assert.Equal(QueueCacheCursorResultKind.CacheMiss, result.Kind);
            Assert.Null(result.Cursor);
            var cacheMiss = Assert.NotNull(result.CacheMiss);
            Assert.Same(requestedToken, cacheMiss.RequestedToken);
            Assert.Equal(2, cacheMiss.LowToken!.SequenceNumber);
            Assert.Equal(2, cacheMiss.HighToken!.SequenceNumber);
            Assert.Equal(1, trackingCache.CreatedCursor!.DisposeCount);
        }

        (trackingCache, cache, stream, requestedToken, retainedToken, retainedCursor) = CreateCacheWithMutationDuringInitialization();
        using (retainedCursor)
        {
#pragma warning disable CS0618 // Verify compatibility of the obsolete wrapper.
            var exception = Assert.Throws<QueueCacheMissException>(() => cache.GetCacheCursor(stream, requestedToken));
#pragma warning restore CS0618

            Assert.Equal(requestedToken.ToString(), exception.Requested);
            Assert.Equal(retainedToken.ToString(), exception.Low);
            Assert.Equal(retainedToken.ToString(), exception.High);
            Assert.Equal(1, trackingCache.CreatedCursor!.DisposeCount);
        }
    }

    [Theory]
    [InlineData(AcquisitionMode.Legacy)]
    [InlineData(AcquisitionMode.Typed)]
    public void CursorAcquisitionDisposesCursorAndPreservesInitializationException(AcquisitionMode mode)
    {
        var trackingCache = new TrackingCache();
        var cache = trackingCache.Cache;
        var stream = StreamId.Create("namespace", Guid.NewGuid());
        cache.AddToCache([new TestBatchContainer(stream, 1)]);
        var expected = new InvalidOperationException("Token comparison failed.");
        var token = new ThrowingCompareToken(1, 2, expected);

        var actual = Record.Exception(() =>
        {
            if (mode == AcquisitionMode.Legacy)
            {
#pragma warning disable CS0618 // Verify compatibility of the obsolete wrapper.
                cache.GetCacheCursor(stream, token);
#pragma warning restore CS0618
            }
            else
            {
                ((IQueueCache)cache).TryGetCacheCursor(stream, token);
            }
        });

        Assert.Same(expected, actual);
        Assert.Equal(1, trackingCache.CreatedCursor!.DisposeCount);
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void EarliestAvailableDisposesCursorWhenInitializationThrows()
    {
        var trackingCache = new TrackingCache();
        var cache = trackingCache.Cache;
        var stream = StreamId.Create("namespace", Guid.NewGuid());
        var expected = new InvalidOperationException("Stream ID access failed.");
        cache.AddToCache([new ThrowingStreamIdBatchContainer(expected)]);

        var actual = Record.Exception(() =>
            ((IQueueCache)cache).TryGetCacheCursorAtPosition(
                stream,
                StreamSubscriptionStartPosition.EarliestAvailable));

        Assert.Same(expected, actual);
        Assert.Equal(1, trackingCache.CreatedCursor!.DisposeCount);
    }

    [Theory]
    [InlineData(AcquisitionMode.Legacy)]
    [InlineData(AcquisitionMode.Typed)]
    [InlineData(AcquisitionMode.EarliestAvailable)]
    public void SuccessfulCursorAcquisitionRemainsCallerOwned(AcquisitionMode mode)
    {
        var trackingCache = new TrackingCache();
        var cache = trackingCache.Cache;
        var stream = StreamId.Create("namespace", Guid.NewGuid());
        cache.AddToCache([new TestBatchContainer(stream, 1)]);

        IQueueCacheCursor cursor;
        if (mode == AcquisitionMode.Legacy)
        {
#pragma warning disable CS0618 // Verify compatibility of the obsolete wrapper.
            cursor = cache.GetCacheCursor(stream, null);
#pragma warning restore CS0618
        }
        else
        {
            var result = mode == AcquisitionMode.Typed
                ? ((IQueueCache)cache).TryGetCacheCursor(stream, null)
                : ((IQueueCache)cache).TryGetCacheCursorAtPosition(
                    stream,
                    StreamSubscriptionStartPosition.EarliestAvailable);
            Assert.Equal(QueueCacheCursorResultKind.Success, result.Kind);
            cursor = Assert.IsAssignableFrom<IQueueCacheCursor>(result.Cursor);
        }

        Assert.Same(trackingCache.CreatedCursor, cursor);
        Assert.Equal(0, trackingCache.CreatedCursor!.DisposeCount);
        cursor.Dispose();
        Assert.Equal(1, trackingCache.CreatedCursor.DisposeCount);
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void EarliestAvailablePreservesDerivedCachePositioning()
    {
        var cache = new DerivedSimpleQueueCache();
        var stream = StreamId.Create("namespace", Guid.NewGuid());
        cache.AddToCache([new TestBatchContainer(stream, 1), new TestBatchContainer(stream, 2)]);

        var result = ((IQueueCache)cache).TryGetCacheCursorAtPosition(
            stream,
            StreamSubscriptionStartPosition.EarliestAvailable);

        Assert.Equal(QueueCacheCursorResultKind.Success, result.Kind);
        Assert.False(cache.GetCacheCursorCalled);
        using var cursor = Assert.IsType<SimpleQueueCacheCursor>(result.Cursor);
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cursor.MoveNextWithResult().Kind);
        Assert.Equal(1, cursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cursor.MoveNextWithResult().Kind);
        Assert.Equal(2, cursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void EarliestAvailablePreservesInheritedCursorBehavior()
    {
        IQueueCache cache = new PressureOverrideSimpleQueueCache();
        var stream = StreamId.Create("namespace", Guid.NewGuid());
        cache.AddToCache([new TestBatchContainer(stream, 1)]);

        var result = cache.TryGetCacheCursorAtPosition(stream, StreamSubscriptionStartPosition.EarliestAvailable);

        Assert.Equal(QueueCacheCursorResultKind.Success, result.Kind);
        using var cursor = Assert.IsType<SimpleQueueCacheCursor>(result.Cursor);
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cursor.MoveNextWithResult().Kind);
        Assert.Equal(1, cursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);
    }

    [Fact]
    public void LatestPositionUsesTypedAcquisition()
    {
        var provider = new TypedAcquisitionQueueCache();
        IQueueCache cache = provider;
        var stream = StreamId.Create("namespace", Guid.NewGuid());

        var result = cache.TryGetCacheCursorAtPosition(stream, StreamSubscriptionStartPosition.Latest);

        Assert.Equal(QueueCacheCursorResultKind.Success, result.Kind);
        Assert.Same(provider.Cursor, result.Cursor);
        Assert.Null(result.CacheMiss);
        Assert.Equal(stream, provider.Request!.Value.StreamId);
        Assert.Null(provider.Request.Value.Token);

        provider.Result = QueueCacheCursorResult<IQueueCacheCursor>.FromCacheMiss(new("requested", "low", "high"));
        result = cache.TryGetCacheCursorAtPosition(stream, StreamSubscriptionStartPosition.Latest);
        Assert.Equal(QueueCacheCursorResultKind.CacheMiss, result.Kind);
        Assert.Null(result.Cursor);
        var miss = Assert.NotNull(result.CacheMiss);
        Assert.Equal("requested", miss.Requested);
        Assert.Equal("low", miss.Low);
        Assert.Equal("high", miss.High);
        provider.Result = QueueCacheCursorResult<IQueueCacheCursor>.NotSupported;
        result = cache.TryGetCacheCursorAtPosition(stream, StreamSubscriptionStartPosition.Latest);
        Assert.Equal(QueueCacheCursorResultKind.NotSupported, result.Kind);
        Assert.Null(result.Cursor);
        Assert.Null(result.CacheMiss);

        provider.Request = null;
        result = cache.TryGetCacheCursorAtPosition(stream, StreamSubscriptionStartPosition.EarliestAvailable);
        Assert.Equal(QueueCacheCursorResultKind.NotSupported, result.Kind);
        Assert.Null(provider.Request);
    }

    [Fact]
    public void TypedPositionPreservesLegacyDefaults()
    {
        IQueueCache cache = new LegacyThrowingQueueCache(new QueueCacheMissException("requested", "low", "high"));

        var latest = cache.TryGetCacheCursorAtPosition(default, StreamSubscriptionStartPosition.Latest);
        Assert.Equal(QueueCacheCursorResultKind.CacheMiss, latest.Kind);
        Assert.Null(latest.Cursor);
        Assert.Equal("requested", Assert.NotNull(latest.CacheMiss).Requested);

        var earliest = cache.TryGetCacheCursorAtPosition(default, StreamSubscriptionStartPosition.EarliestAvailable);
        Assert.Equal(QueueCacheCursorResultKind.NotSupported, earliest.Kind);
        Assert.Null(earliest.Cursor);
        Assert.Null(earliest.CacheMiss);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => cache.TryGetCacheCursorAtPosition(default, (StreamSubscriptionStartPosition)123));
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void LegacyAcquisitionPreservesProviderException()
    {
        var innerException = new InvalidOperationException("inner");
        var expected = new QueueCacheMissException("provider message", innerException);
        IQueueCache cache = new LegacyThrowingQueueCache(expected);

#pragma warning disable CS0618 // Verify compatibility of the obsolete wrapper.
        var actual = Assert.Throws<QueueCacheMissException>(
            () => cache.GetCacheCursor(default, null));
#pragma warning restore CS0618

        Assert.Same(expected, actual);
        Assert.Same(innerException, actual.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LatestPositionPropagatesUnexpectedAcquisitionErrors(bool notSupported)
    {
        Exception expected = notSupported
            ? new NotSupportedException("Token acquisition failure")
            : new InvalidOperationException("Provider failure");
        IQueueCache cache = new LegacyThrowingQueueCache(expected);

        var actual = Record.Exception(
            () => cache.TryGetCacheCursorAtPosition(default, StreamSubscriptionStartPosition.Latest));

        Assert.Same(expected, actual);
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
        Assert.Same(cursor, cache.Cursor);
        Assert.Equal(1, cursor.DisposeCount);
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
        Assert.Same(cursor, cache.Cursor);
        Assert.Equal(1, cursor.DisposeCount);
    }

    private sealed class DerivedCursorSimpleQueueCache() : SimpleQueueCache(10, NullLogger.Instance)
    {
        public DerivedSimpleQueueCacheCursor? Cursor { get; private set; }
        public int GetCacheCursorCount { get; private set; }

        [Obsolete("Use IQueueCache.TryGetCacheCursor instead.")]
        public override IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
        {
            GetCacheCursorCount++;
            var cursor = new DerivedSimpleQueueCacheCursor(this, streamId);
            if (InitializeCursor(cursor, token) is { } cacheMiss)
            {
                throw cacheMiss.ToException();
            }

            return Cursor = cursor;
        }
    }

    private sealed class DerivedSimpleQueueCacheCursor(
        SimpleQueueCache cache,
        StreamId streamId) : SimpleQueueCacheCursor(cache, streamId, NullLogger.Instance)
    {
        public Exception? NextMoveException { get; set; }
        public int MoveNextCount { get; private set; }
        public int GetCurrentCount { get; private set; }
        public int DisposeCount { get; private set; }

        [Obsolete("Use MoveNextWithResult instead.")]
        public override bool MoveNext()
        {
            MoveNextCount++;
            if (NextMoveException is { } exception)
            {
                NextMoveException = null;
                throw exception;
            }

            return base.MoveNext();
        }

        public override IBatchContainer? GetCurrent(out Exception? exception)
        {
            GetCurrentCount++;
            return base.GetCurrent(out exception);
        }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }
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

    private sealed class PressureOverrideSimpleQueueCache() : SimpleQueueCache(10, NullLogger.Instance)
    {
        public override bool IsUnderPressure() => false;
    }

    private sealed class TypedAcquisitionQueueCache : IQueueCache
    {
        public IQueueCacheCursor Cursor { get; } = new EmptyCursor();
        public QueueCacheCursorResult<IQueueCacheCursor>? Result { get; set; }
        public (StreamId StreamId, StreamSequenceToken? Token)? Request { get; set; }

        public void AddToCache(IList<IBatchContainer> messages)
        {
        }

        public int GetMaxAddCount() => 1;

        public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
            => throw new InvalidOperationException("Positioning must use typed acquisition.");

        QueueCacheCursorResult<IQueueCacheCursor> IQueueCache.TryGetCacheCursor(
            StreamId streamId,
            StreamSequenceToken? token)
        {
            Request = (streamId, token);
            return Result ?? QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(Cursor);
        }

        public bool IsUnderPressure() => false;

        public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
        {
            purgedItems = null!;
            return false;
        }
    }

    private sealed class LegacyThrowingQueueCache(Exception exception) : IQueueCache
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
        TrackingCache TrackingCache,
        SimpleQueueCache Cache,
        StreamId Stream,
        StreamSequenceToken RequestedToken,
        StreamSequenceToken RetainedToken,
        IQueueCacheCursor RetainedCursor)
        CreateCacheWithMutationDuringInitialization()
    {
        var trackingCache = new TrackingCache();
        var cache = trackingCache.Cache;
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
        return (trackingCache, cache, stream, requestedToken, retainedToken, retainedCursor);
    }

    public enum AcquisitionMode
    {
        Legacy,
        Typed,
        EarliestAvailable,
    }

    private sealed class TrackingCache
    {
        public TrackingCache()
        {
            Cache = new SimpleQueueCache(
                10,
                NullLogger.Instance,
                (cache, streamId) => CreatedCursor = new(cache, streamId));
        }

        public SimpleQueueCache Cache { get; }
        public TrackingSimpleQueueCacheCursor? CreatedCursor { get; private set; }
    }

    private sealed class TrackingSimpleQueueCacheCursor(
        SimpleQueueCache cache,
        StreamId streamId) : SimpleQueueCacheCursor(cache, streamId, NullLogger.Instance)
    {
        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingCompareToken(
        long sequenceNumber,
        int throwOnComparison,
        Exception exception) : StreamSequenceToken
    {
        private int _comparisons;

        public override long SequenceNumber
        {
            get => sequenceNumber;
            protected set => throw new NotSupportedException();
        }

        public override int EventIndex
        {
            get => 0;
            protected set => throw new NotSupportedException();
        }

        public override int CompareTo(StreamSequenceToken? other)
        {
            if (Interlocked.Increment(ref _comparisons) == throwOnComparison)
            {
                throw exception;
            }

            return other is null ? 1 : sequenceNumber.CompareTo(other.SequenceNumber);
        }

        public override bool Equals(StreamSequenceToken? other)
            => other is not null && sequenceNumber == other.SequenceNumber && EventIndex == other.EventIndex;
    }

    private sealed class ThrowingStreamIdBatchContainer(Exception exception) : IBatchContainer
    {
        public StreamId StreamId => throw exception;
        public StreamSequenceToken SequenceToken { get; } = new EventSequenceTokenV2(1);
        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => [];
        public bool ImportRequestContext() => false;
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
        public int DisposeCount { get; private set; }

        public override void Dispose() => DisposeCount++;

        public override QueueCacheCursorMoveResult MoveNextWithResult() => default;
    }

    private sealed class ThrowingMoveResultCursor : EmptyCursor
    {
        public int DisposeCount { get; private set; }

        public override void Dispose() => DisposeCount++;

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

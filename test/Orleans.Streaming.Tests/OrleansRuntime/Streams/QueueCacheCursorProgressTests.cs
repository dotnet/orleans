using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
[TestCategory("BVT")]
[TestCategory("Streaming")]
public class QueueCacheCursorProgressTests
{
    private static readonly StreamId Target = StreamId.Create("progress", "target");
    private static readonly StreamId Other = StreamId.Create("progress", "other");

    [Fact]
    public void SimpleCursorRejectsForeignResumeDomainsBeforeFiltering()
    {
        var cache = new CacheHarness(pooled: false);
        cache.Add(new TestBatch(Target, 1, [0, 1, 2]));
        using var cursor = cache.GetCursor(new EventSequenceTokenV2(1));
        var progress = (IQueueCacheCursorProgress)cursor;
        progress.SetDeliveredThrough(new ForeignResumeToken(1, 1));

        Assert.Throws<ArgumentOutOfRangeException>(() => cursor.MoveNextWithResult());
        Assert.Null(progress.SafeSequenceToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResumeFilterPreservesLaterRecordsInTheSameDeliveryBatch(bool pooled)
    {
        var cache = new CacheHarness(pooled);
        cache.Add(new TestBatch(Target, 1, [0, 1, 2]), new TestBatch(Target, 2, [0, 1, 2]));
        using var cursor = cache.GetCursor(new EventSequenceTokenV2(1));
        var progress = (IQueueCacheCursorProgress)cursor;
        progress.SetDeliveredThrough(new EventSequenceTokenV2(1, 1));

        Read(cursor, 1);
        Assert.Equal([2], Assert.IsType<TestBatch>(cursor.GetCurrent(out _)).Indices);
        Read(cursor, 2);
        Assert.Equal([0, 1, 2], Assert.IsType<TestBatch>(cursor.GetCurrent(out _)).Indices);
        Assert.Null(progress.SafeSequenceToken);
        progress.RecordDeliveryCompletion();
        Assert.Equal(2, progress.SafeSequenceToken?.SequenceNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FutureInclusivePositionFiltersEarlierRecordsAndPreservesEventIndex(bool pooled)
    {
        var cache = new CacheHarness(pooled);
        cache.Add(new TestBatch(Target, 1, [0, 1, 2]));
        using var cursor = cache.GetCursor(new EventSequenceTokenV2(2, 1));
        var progress = (IQueueCacheCursorProgress)cursor;
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);
        Assert.Equal(1, progress.SafeSequenceToken?.SequenceNumber);

        cache.Add(new TestBatch(Target, 2, [0, 1, 2]));
        cursor.Refresh(new EventSequenceTokenV2(2, 0));
        Read(cursor, 2);
        Assert.Equal([1, 2], Assert.IsType<TestBatch>(cursor.GetCurrent(out _)).Indices);
        Assert.Equal(1, progress.SafeSequenceToken?.SequenceNumber);
        progress.RecordDeliveryCompletion();
        Assert.Equal(2, progress.SafeSequenceToken?.SequenceNumber);
    }

    private sealed class ForeignResumeToken(long sequenceNumber, int eventIndex)
        : EventSequenceTokenV2(sequenceNumber, eventIndex);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawPooledCursorExposesProgressAndOwnerBackedFailureRewind(bool earliestAvailable)
    {
        var harness = new CacheHarness(pooled: true);
        harness.Add(new(Other, 1), new(Target, 2), new(Other, 3), new(Target, 4), new(Other, 5));
        var cache = harness.Pooled!;
        var result = earliestAvailable
            ? cache.TryGetCursorAtPosition(Target, StreamSubscriptionStartPosition.EarliestAvailable)
            : cache.TryGetCursor(Target, new EventSequenceTokenV2(1));
        Assert.Equal(QueueCacheCursorResultKind.Success, result.Kind);
        var cursor = Assert.IsAssignableFrom<object>(result.Cursor);
        var progress = Assert.IsAssignableFrom<IQueueCacheCursorProgress>(cursor);
        progress.EnableDeliveryProgress();

        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cache.TryGetNextMessageWithResult(cursor, out var first).Kind);
        Assert.Equal(2, first!.SequenceToken.SequenceNumber);
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cache.TryGetNextMessageWithResult(cursor, out _).Kind);
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cache.TryGetNextMessageWithResult(cursor, out _).Kind);
        Assert.Equal(1, progress.SafeSequenceToken?.SequenceNumber);

        // No concrete cursor type or cache helper is required for failure forwarding.
        progress.RecordDeliveryFailure();
        Assert.Equal(1, progress.SafeSequenceToken?.SequenceNumber);
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cache.TryGetNextMessageWithResult(cursor, out var retry).Kind);
        Assert.Equal(2, retry!.SequenceToken.SequenceNumber);

        progress.RecordDeliveryFailure();
        progress.SetDeliveredThrough(new EventSequenceTokenV2(2));
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cache.TryGetNextMessageWithResult(cursor, out var after).Kind);
        Assert.Equal(4, after!.SequenceToken.SequenceNumber);
        Assert.Equal(3, progress.SafeSequenceToken?.SequenceNumber);
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cache.TryGetNextMessageWithResult(cursor, out _).Kind);
        Assert.Equal(3, progress.SafeSequenceToken?.SequenceNumber);
        progress.RecordDeliveryCompletion();
        Assert.Equal(5, progress.SafeSequenceToken?.SequenceNumber);
        Assert.Equal(cache.GetSafeSequenceToken(cursor), progress.SafeSequenceToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SafePrefixWaitsForAllSelectedBatchesAndCrossesUnrelatedRecords(bool pooled)
    {
        var cache = new CacheHarness(pooled);
        cache.Add(new(Other, 1), new(Target, 2), new(Other, 3), new(Target, 4), new(Other, 5));
        using var cursor = cache.GetCursor(StreamSubscriptionStartPosition.EarliestAvailable);
        var progress = Assert.IsAssignableFrom<IQueueCacheCursorProgress>(cursor);

        Read(cursor, 2);
        Assert.Equal(1, progress.SafeSequenceToken?.SequenceNumber);
        Read(cursor, 4);
        Assert.Equal(1, progress.SafeSequenceToken?.SequenceNumber);
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);
        Assert.Equal(1, progress.SafeSequenceToken?.SequenceNumber);

        progress.RecordDeliveryCompletion();
        Assert.Equal(5, progress.SafeSequenceToken?.SequenceNumber);
        cache.Add(new TestBatch(Other, 6));
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);
        Assert.Equal(6, progress.SafeSequenceToken?.SequenceNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureRewindsFirstPendingBatchAndPreservesEarlierSafePrefix(bool pooled)
    {
        var cache = new CacheHarness(pooled);
        cache.Add(new(Other, 1), new(Target, 2), new(Other, 3), new(Target, 4), new(Other, 5));
        using var cursor = cache.GetCursor(StreamSubscriptionStartPosition.EarliestAvailable);
        var progress = (IQueueCacheCursorProgress)cursor;
        Read(cursor, 2);
        Read(cursor, 4);
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);
        if (cache.Simple is { } simple)
        {
            // The first pending matching record is pinned, even after moving to NoData.
            Assert.True(simple.TryPurgeFromCache(out var purged));
            Assert.Equal(1, Assert.Single(purged).SequenceToken.SequenceNumber);
        }

        ((IQueueCacheCursorProgress)cursor).RecordDeliveryFailure();
        Assert.Equal(1, progress.SafeSequenceToken?.SequenceNumber);
        progress.RecordDeliveryCompletion(); // No selected retry has been confirmed yet.
        Assert.Equal(1, progress.SafeSequenceToken?.SequenceNumber);
        if (cache.Simple is { } rewound)
        {
            Assert.False(rewound.TryPurgeFromCache(out _));
        }

        Read(cursor, 2);
        Read(cursor, 4);
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);
        Assert.Equal(1, progress.SafeSequenceToken?.SequenceNumber);
        progress.RecordDeliveryCompletion();
        Assert.Equal(5, progress.SafeSequenceToken?.SequenceNumber);
        if (cache.Simple is { } acknowledged)
        {
            Assert.True(acknowledged.TryPurgeFromCache(out var purged));
            // Retrying does not erase the original receipt's DeliveryFailure marking.
            Assert.Equal(new long[] { 3, 5 }, purged.Select(batch => batch.SequenceToken.SequenceNumber));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonemptyLatestExcludesEntireObservedTailWithoutSelectingIt(bool pooled)
    {
        var cache = new CacheHarness(pooled);
        cache.Add(new(Target, 1), new(Other, 2));
        using var cursor = cache.GetCursor(StreamSubscriptionStartPosition.Latest);
        var progress = (IQueueCacheCursorProgress)cursor;
        Assert.Equal(2, progress.SafeSequenceToken?.SequenceNumber);
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);
        ((IQueueCacheCursorProgress)cursor).RecordDeliveryFailure(); // Latest did not leave a synthetic pending delivery.
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);

        cache.Add(new(Target, 3), new(Other, 4), new(Target, 5));
        Read(cursor, 3);
        Assert.Equal(2, progress.SafeSequenceToken?.SequenceNumber);
        progress.RecordDeliveryCompletion();
        Read(cursor, 5);
        Assert.Equal(4, progress.SafeSequenceToken?.SequenceNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyLatestIncludesFirstFutureRecordAndNoDataCertifiesNothing(bool pooled)
    {
        var cache = new CacheHarness(pooled);
        using var cursor = cache.GetCursor(StreamSubscriptionStartPosition.Latest);
        var progress = (IQueueCacheCursorProgress)cursor;
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);
        progress.RecordDeliveryCompletion();
        Assert.Null(progress.SafeSequenceToken);
        cache.Add(new(Target, 1), new(Other, 2), new(Target, 3));
        Read(cursor, 1);
        Assert.Null(progress.SafeSequenceToken);
        progress.RecordDeliveryCompletion();
        Read(cursor, 3);
        Assert.Equal(2, progress.SafeSequenceToken?.SequenceNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WaitingForFutureTokenDoesNotCertifyTheRequestedGap(bool pooled)
    {
        var cache = new CacheHarness(pooled);
        using var cursor = cache.GetCursor(new EventSequenceTokenV2(10));
        var progress = (IQueueCacheCursorProgress)cursor;
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);
        progress.RecordDeliveryCompletion();
        Assert.Null(progress.SafeSequenceToken);
        cache.Add(new TestBatch(Other, 3));
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);
        Assert.Equal(3, progress.SafeSequenceToken?.SequenceNumber);
        cache.Add(new TestBatch(Target, 10));
        Read(cursor, 10);
        Assert.Equal(3, progress.SafeSequenceToken?.SequenceNumber);
        progress.RecordDeliveryCompletion();
        Assert.Equal(10, progress.SafeSequenceToken?.SequenceNumber);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InclusiveAndAfterTokenSlicingSurviveFailureAndRetry(bool pooled, bool afterToken)
    {
        var cache = new CacheHarness(pooled);
        cache.Add(new TestBatch(Target, 10, [0, 1, 2]));
        var boundary = new EventSequenceTokenV2(10, 1);
        using var cursor = cache.GetCursor(boundary);
        var progress = (IQueueCacheCursorProgress)cursor;
        if (afterToken)
        {
            progress.SetDeliveredThrough(boundary);
        }

        var expected = afterToken ? new[] { 2 } : new[] { 1, 2 };
        Assert.Equal(expected, Assert.IsType<TestBatch>(Read(cursor, 10)).Indices);
        Assert.Null(progress.SafeSequenceToken);
        ((IQueueCacheCursorProgress)cursor).RecordDeliveryFailure();
        Assert.Equal(expected, Assert.IsType<TestBatch>(Read(cursor, 10)).Indices);
        Assert.Null(progress.SafeSequenceToken);
        progress.RecordDeliveryCompletion();
        Assert.Equal(expected[0], progress.SafeSequenceToken?.EventIndex);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SelectionExceptionDoesNotSkipFirstPendingMatchingRecord(bool pooled, bool earlierPending)
    {
        var cache = new CacheHarness(pooled);
        var first = new TestBatch(Target, 2);
        var second = new TestBatch(Target, 4);
        cache.Add(new(Other, 1), first, new(Other, 3), second, new(Other, 5));
        using var cursor = cache.GetCursor(new EventSequenceTokenV2(1));
        var progress = (IQueueCacheCursorProgress)cursor;
        var failingBatch = earlierPending ? second : first;
        if (pooled)
        {
            cache.Adapter.FailMaterializationAt = failingBatch.Number;
        }
        else
        {
            failingBatch.FailNextFilter = true;
        }

        if (earlierPending)
        {
            Read(cursor, 2);
        }

        Assert.Throws<InvalidOperationException>(() => cursor.MoveNextWithResult());
        Assert.Equal(1, progress.SafeSequenceToken?.SequenceNumber);
        ((IQueueCacheCursorProgress)cursor).RecordDeliveryFailure();
        Read(cursor, 2);
        Read(cursor, 4);
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);
        Assert.Equal(1, progress.SafeSequenceToken?.SequenceNumber);
        progress.RecordDeliveryCompletion();
        Assert.Equal(5, progress.SafeSequenceToken?.SequenceNumber);
    }

    [Fact]
    public void PooledMoveTokenExceptionDoesNotCommitTheNextPosition()
    {
        var cache = new CacheHarness(pooled: true);
        cache.Add(new(Other, 1), new(Target, 2), new(Other, 3));
        using var cursor = cache.GetCursor(StreamSubscriptionStartPosition.EarliestAvailable);
        var progress = (IQueueCacheCursorProgress)cursor;
        cache.Adapter.FailTokenAt = 3;
        Assert.Throws<InvalidOperationException>(() => cursor.MoveNextWithResult());
        Assert.Equal(1, progress.SafeSequenceToken?.SequenceNumber);
        ((IQueueCacheCursorProgress)cursor).RecordDeliveryFailure();
        Read(cursor, 2);
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);
        Assert.Equal(1, progress.SafeSequenceToken?.SequenceNumber);
        progress.RecordDeliveryCompletion();
        Assert.Equal(3, progress.SafeSequenceToken?.SequenceNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownCacheMissDoesNotInventSafeProgress(bool pooled)
    {
        var cache = new CacheHarness(pooled);
        using var cursor = cache.GetCursor(new EventSequenceTokenV2(1));
        var progress = (IQueueCacheCursorProgress)cursor;
        cache.Add(new TestBatch(Target, 5));
        var observed = cursor.MoveNextWithResult();
        Assert.Equal(QueueCacheCursorMoveResultKind.CacheMiss, observed.Kind);
        var initialMiss = Assert.NotNull(observed.CacheMiss);
        Assert.Equal(1, initialMiss.RequestedToken?.SequenceNumber);
        Assert.Equal(5, initialMiss.LowToken?.SequenceNumber);
        Assert.Equal(5, initialMiss.HighToken?.SequenceNumber);
        Assert.Throws<QueueCacheMissException>(() => progress.RecordDeliveryFailure());
        progress.RecordDeliveryCompletion();
        Assert.Null(progress.SafeSequenceToken);
        if (cache.Pooled is { } native)
        {
            native.RemoveOldestMessage();
        }
        else
        {
            Assert.True(cache.Simple!.TryPurgeFromCache(out _));
        }

        // Empty cache, ACK, refresh, and later admission cannot reconcile observed loss.
        AssertSameCacheMiss(observed, cursor.MoveNextWithResult());
        progress.RecordDeliveryCompletion();
        Assert.Throws<QueueCacheMissException>(() => cursor.Refresh(new EventSequenceTokenV2(6)));
        cache.Add(new TestBatch(Target, 6));
        AssertSameCacheMiss(observed, cursor.MoveNextWithResult());
        Assert.Null(progress.SafeSequenceToken);

        // A deliberate new start policy is distinct from resuming the failed cursor.
        using var reconciled = cache.GetCursor(StreamSubscriptionStartPosition.EarliestAvailable);
        Read(reconciled, 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PooledActiveCursorPurgedToEmptyReturnsStickyTypedMissWithoutPurgeMetadata(bool earliestAvailable)
    {
        var cache = new CacheHarness(pooled: true);
        cache.Add(new(Other, 1), new(Target, 2), new(Target, 3));
        using var cursor = earliestAvailable
            ? cache.GetCursor(StreamSubscriptionStartPosition.EarliestAvailable)
            : cache.GetCursor(new EventSequenceTokenV2(1));
        var progress = (IQueueCacheCursorProgress)cursor;
        Read(cursor, 2); // The unread position is now record 3; the safe prefix is 1.
        while (!cache.Pooled!.IsEmpty)
        {
            cache.Pooled.RemoveOldestMessage();
        }

        var observed = cursor.MoveNextWithResult();
        Assert.Equal(QueueCacheCursorMoveResultKind.CacheMiss, observed.Kind);
        var miss = Assert.NotNull(observed.CacheMiss);
        Assert.Equal(new EventSequenceTokenV2(3).ToString(), miss.Requested);
        Assert.Null(miss.Low);
        Assert.Null(miss.High);
        Assert.Throws<QueueCacheMissException>(() => progress.RecordDeliveryFailure());
        progress.RecordDeliveryCompletion();
        Assert.Equal(1, progress.SafeSequenceToken?.SequenceNumber);
        AssertSameCacheMiss(observed, cursor.MoveNextWithResult());
        cache.Add(new TestBatch(Target, 4));
        AssertSameCacheMiss(observed, cursor.MoveNextWithResult());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PooledCursorCannotLoseItsInitialPositionToEmptyCache(bool earliestAvailable)
    {
        var cache = new CacheHarness(pooled: true);
        cache.Add(new TestBatch(Target, 1));
        using var cursor = earliestAvailable
            ? cache.GetCursor(StreamSubscriptionStartPosition.EarliestAvailable)
            : cache.GetCursor(new EventSequenceTokenV2(1));
        cache.Pooled!.RemoveOldestMessage();

        Assert.Equal(QueueCacheCursorMoveResultKind.CacheMiss, cursor.MoveNextWithResult().Kind);
        Assert.Null(((IQueueCacheCursorProgress)cursor).SafeSequenceToken);
    }

    [Fact]
    public void PooledRetryGapRemainsExplicitAcrossEmptyCacheAndRepeatedFailures()
    {
        var cache = new CacheHarness(pooled: true);
        cache.Add(new(Other, 1), new(Target, 2), new(Other, 3));
        using var cursor = cache.GetCursor(StreamSubscriptionStartPosition.EarliestAvailable);
        var progress = (IQueueCacheCursorProgress)cursor;
        Read(cursor, 2);
        while (!cache.Pooled!.IsEmpty)
        {
            cache.Pooled.RemoveOldestMessage();
        }

        Assert.Throws<QueueCacheMissException>(() => progress.RecordDeliveryFailure());
        Assert.Equal(QueueCacheCursorMoveResultKind.CacheMiss, cursor.MoveNextWithResult().Kind);
        progress.RecordDeliveryCompletion();
        Assert.Equal(1, progress.SafeSequenceToken?.SequenceNumber);
        cache.Add(new TestBatch(Other, 5));
        Assert.Equal(QueueCacheCursorMoveResultKind.CacheMiss, cursor.MoveNextWithResult().Kind);
        Assert.Throws<QueueCacheMissException>(() => progress.RecordDeliveryFailure());
        progress.RecordDeliveryCompletion();
        Assert.Equal(QueueCacheCursorMoveResultKind.CacheMiss, cursor.MoveNextWithResult().Kind);
        Assert.Equal(1, progress.SafeSequenceToken?.SequenceNumber);
    }

    [Fact]
    public void SimpleGetCurrentExceptionRewindsWhilePendingReceiptRemainsPinned()
    {
        var cache = new SimpleQueueCache(10, NullLogger.Instance);
        cache.AddToCache([new TestBatch(Other, 1), new TestBatch(Target, 2), new TestBatch(Other, 3)]);
        using var cursor = new ThrowingCurrentCursor(cache);
        Assert.Null(cache.InitializeCursor(cursor, new EventSequenceTokenV2(1)));
        var progress = (IQueueCacheCursorProgress)cursor;
        progress.EnableDeliveryProgress();
        var delivery = (IQueueCacheCursorBatchDelivery)cursor;
        using (delivery.ProtectDeliveryBatch())
        {
            Assert.Equal(QueueCacheCursorMoveResultKind.Success, cursor.MoveNextWithResult().Kind);
            Assert.Throws<InvalidOperationException>(() => cursor.GetCurrent(out _));
        }

        Assert.True(cache.TryPurgeFromCache(out var purged));
        Assert.Equal(1, Assert.Single(purged).SequenceToken.SequenceNumber);
        progress.RecordDeliveryFailure();
        Assert.False(cache.TryPurgeFromCache(out _));
        Read(cursor, 2);
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);
        progress.RecordDeliveryCompletion();
        Assert.Equal(3, progress.SafeSequenceToken?.SequenceNumber);
        Assert.True(cache.TryPurgeFromCache(out purged));
        Assert.Equal(3, Assert.Single(purged).SequenceToken.SequenceNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SimpleFilteredBatchFailurePreservesReceiptSkipAndCertifiedReplay(bool certifiedReplay)
    {
        var cache = new CacheHarness(pooled: false);
        cache.Add(new TestBatch(Target, 10, [0, 1, 2]));
        using var cursor = cache.GetCursor(new EventSequenceTokenV2(10, 1));
        var delivery = (IQueueCacheCursorBatchDelivery)cursor;
        using (delivery.ProtectDeliveryBatch())
        {
            var selected = Read(cursor, 10);
            if (certifiedReplay)
            {
                ((IQueueCacheCursorProgress)cursor).RecordDeliveryFailure();
            }
            else
            {
                delivery.RecordDeliveryFailure(selected);
            }
        }

        if (certifiedReplay)
        {
            Assert.Equal(new[] { 1, 2 }, Assert.IsType<TestBatch>(Read(cursor, 10)).Indices);
        }
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);
        ((IQueueCacheCursorProgress)cursor).RecordDeliveryCompletion();
        Assert.True(cache.Simple!.TryPurgeFromCache(out var purged));
        Assert.Empty(purged);
    }

    [Fact]
    public void ReceiptPooledCursorPreservesObservedLossWithoutCertifiedTracking()
    {
        var cache = new CacheHarness(pooled: true);
        cache.Add(new(Other, 1), new(Target, 2), new(Target, 3), new(Other, 4));
        using var cursor = cache.GetCursor(StreamSubscriptionStartPosition.EarliestAvailable, trackProgress: false);
        Read(cursor, 2);
        Assert.Null(((IQueueCacheCursorProgress)cursor).SafeSequenceToken);
        for (var i = 0; i < 3; i++) cache.Pooled!.RemoveOldestMessage();

        var miss = cursor.MoveNextWithResult();
        Assert.Equal(QueueCacheCursorMoveResultKind.CacheMiss, miss.Kind);
        Assert.Equal(3, miss.CacheMiss!.Value.RequestedToken!.SequenceNumber);
        cache.Add(new TestBatch(Target, 5));
        AssertSameCacheMiss(miss, cursor.MoveNextWithResult());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReceiptSimpleCursorReleasesPinsAndMarksOnlyFailedReceipts(bool grouped)
    {
        var cache = new CacheHarness(pooled: false);
        var unrelated = new TestBatch(Other, 2);
        cache.Add(new(Target, 1), unrelated, new(Target, 3));
        using var cursor = cache.GetCursor(new EventSequenceTokenV2(1), trackProgress: false);
        var delivery = (IQueueCacheCursorBatchDelivery)cursor;
        using (grouped ? delivery.ProtectDeliveryBatch() : null)
        {
            var first = Read(cursor, 1);
            if (grouped)
            {
                var last = Read(cursor, 3);
                Assert.Throws<InvalidOperationException>(() => delivery.RecordDeliveryFailure(unrelated));
                delivery.RecordDeliveryFailure(new BatchContainerBatch([first, last]));
            }
            else
            {
                cursor.RecordDeliveryFailure();
                Read(cursor, 3);
            }
            Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cursor.MoveNextWithResult().Kind);
        }

        Assert.Null(((IQueueCacheCursorProgress)cursor).SafeSequenceToken);
        Assert.True(cache.Simple!.TryPurgeFromCache(out var purged));
        Assert.Equal(grouped ? new long[] { 2 } : [2, 3], purged.Select(batch => batch.SequenceToken.SequenceNumber));
        Assert.Equal(0, cache.Simple.Size);
    }

    private static void AssertSameCacheMiss(QueueCacheCursorMoveResult expected, QueueCacheCursorMoveResult actual)
    {
        Assert.Equal(QueueCacheCursorMoveResultKind.CacheMiss, expected.Kind);
        Assert.Equal(QueueCacheCursorMoveResultKind.CacheMiss, actual.Kind);
        var expectedMiss = Assert.NotNull(expected.CacheMiss);
        var actualMiss = Assert.NotNull(actual.CacheMiss);
        Assert.Equal(expectedMiss.Requested, actualMiss.Requested);
        Assert.Equal(expectedMiss.Low, actualMiss.Low);
        Assert.Equal(expectedMiss.High, actualMiss.High);
        Assert.Same(expectedMiss.RequestedToken, actualMiss.RequestedToken);
        Assert.Same(expectedMiss.LowToken, actualMiss.LowToken);
        Assert.Same(expectedMiss.HighToken, actualMiss.HighToken);
    }

    private static IBatchContainer Read(IQueueCacheCursor cursor, long expectedSequence)
    {
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cursor.MoveNextWithResult().Kind);
        var batch = Assert.IsAssignableFrom<IBatchContainer>(cursor.GetCurrent(out var exception));
        Assert.Null(exception);
        Assert.Equal(expectedSequence, batch.SequenceToken.SequenceNumber);
        return batch;
    }

    private sealed class CacheHarness
    {
        public SimpleQueueCache? Simple { get; }
        public PooledQueueCache? Pooled { get; }
        public TestAdapter Adapter { get; } = new();

        public CacheHarness(bool pooled)
        {
            if (pooled)
            {
                Pooled = new PooledQueueCache(Adapter, NullLogger.Instance, null, null);
            }
            else
            {
                Simple = new SimpleQueueCache(10, NullLogger.Instance);
            }
        }

        public void Add(params TestBatch[] batches)
        {
            if (Simple is { } simple)
            {
                simple.AddToCache(batches);
                return;
            }

            foreach (var batch in batches)
            {
                Adapter.Batches.Add(batch.Number, batch);
            }

            Pooled!.Add(batches.Select(batch => new CachedMessage
            {
                StreamId = batch.StreamId,
                SequenceNumber = batch.Number,
                EnqueueTimeUtc = DateTime.UnixEpoch,
                DequeueTimeUtc = DateTime.UnixEpoch,
            }).ToList(), DateTime.UnixEpoch);
        }

        public IQueueCacheCursor GetCursor(StreamSubscriptionStartPosition position, bool trackProgress = true)
            => ConfigureProgress(Simple is { } simple
                ? ((IQueueCache)simple).TryGetCacheCursorAtPosition(Target, position).Cursor!
                : new PooledCursor(Pooled!, Pooled!.TryGetCursorAtPosition(Target, position).Cursor!), trackProgress);

        public IQueueCacheCursor GetCursor(StreamSequenceToken token, bool trackProgress = true)
            => ConfigureProgress(Simple is { } simple
                ? ((IQueueCache)simple).TryGetCacheCursor(Target, token).Cursor!
                : new PooledCursor(Pooled!, Pooled!.TryGetCursor(Target, token).Cursor!), trackProgress);

        private static IQueueCacheCursor ConfigureProgress(IQueueCacheCursor cursor, bool trackProgress)
        {
            if (trackProgress) ((IQueueCacheCursorProgress)cursor).EnableDeliveryProgress();
            return cursor;
        }
    }

    private sealed class PooledCursor(PooledQueueCache cache, object cursor) : IQueueCacheCursor, IQueueCacheCursorProgress
    {
        private IBatchContainer? current;
        public StreamSequenceToken? SafeSequenceToken => cache.GetSafeSequenceToken(cursor);
        public void EnableDeliveryProgress() => cache.EnableDeliveryProgress(cursor);
        public void SetDeliveredThrough(StreamSequenceToken token) => cache.SetCursorDeliveredThrough(cursor, token);
        public void RecordDeliveryCompletion() => cache.RecordDeliveryCompletion(cursor);
        public void RecordDeliveryFailure() => cache.RecordDeliveryFailure(cursor);
        public void Refresh(StreamSequenceToken token) => cache.Refresh(cursor, token);
        public void Dispose() { }
        public bool MoveNext() => MoveNextWithResult().Kind == QueueCacheCursorMoveResultKind.Success;
        public QueueCacheCursorMoveResult MoveNextWithResult() => cache.TryGetNextMessageWithResult(cursor, out current);
        public IBatchContainer? GetCurrent(out Exception? exception)
        {
            exception = null;
            return current;
        }
    }

    private sealed class TestAdapter : ICacheDataAdapter
    {
        public Dictionary<long, TestBatch> Batches { get; } = [];
        public long? FailMaterializationAt { get; set; }
        public long? FailTokenAt { get; set; }
        public IBatchContainer GetBatchContainer(ref CachedMessage message)
        {
            if (FailMaterializationAt == message.SequenceNumber)
            {
                FailMaterializationAt = null;
                throw new InvalidOperationException("Transient materialization failure.");
            }

            return Batches[message.SequenceNumber];
        }

        public StreamSequenceToken GetSequenceToken(ref CachedMessage message)
        {
            if (FailTokenAt == message.SequenceNumber)
            {
                FailTokenAt = null;
                throw new InvalidOperationException("Transient token materialization failure.");
            }

            return new EventSequenceTokenV2(message.SequenceNumber);
        }

        public int Compare(ref CachedMessage message, StreamSequenceToken token) => message.SequenceNumber.CompareTo(token.SequenceNumber);
    }

    private sealed class TestBatch(StreamId stream, long number, int[]? indices = null) : IBatchContainer, IQueueCacheBatchContainerFilter
    {
        public long Number { get; } = number;
        public int[] Indices { get; } = indices ?? [0];
        public bool FailNextFilter { get; set; }
        public StreamId StreamId => stream;
        public StreamSequenceToken SequenceToken => new EventSequenceTokenV2(Number, Indices[0]);
        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => [];
        public bool ImportRequestContext() => false;
        public IBatchContainer? FilterFrom(StreamSequenceToken token)
        {
            if (FailNextFilter)
            {
                FailNextFilter = false;
                throw new InvalidOperationException("Transient filtering failure.");
            }

            if (token.SequenceNumber != Number) return token.SequenceNumber < Number ? this : null;
            var remaining = Indices.Where(index => index >= token.EventIndex).ToArray();
            return remaining.Length == 0 ? null : new TestBatch(stream, Number, remaining);
        }

        public IBatchContainer? FilterAfter(StreamSequenceToken token)
        {
            if (token.SequenceNumber != Number) return token.SequenceNumber < Number ? this : null;
            var remaining = Indices.Where(index => index > token.EventIndex).ToArray();
            return remaining.Length == 0 ? null : new TestBatch(stream, Number, remaining);
        }
    }

    private sealed class ThrowingCurrentCursor(SimpleQueueCache cache) : SimpleQueueCacheCursor(cache, Target, NullLogger.Instance)
    {
        private bool fail = true;
        public override IBatchContainer? GetCurrent(out Exception? exception)
        {
            if (fail)
            {
                fail = false;
                throw new InvalidOperationException("Transient GetCurrent failure.");
            }

            return base.GetCurrent(out exception);
        }
    }
}

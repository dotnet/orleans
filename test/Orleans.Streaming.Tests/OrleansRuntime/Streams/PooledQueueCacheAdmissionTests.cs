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
public class PooledQueueCacheAdmissionTests
{
    private static readonly StreamId Stream = StreamId.Create("admission", "target");
    // An explicit timestamp beyond the initial reporting deadline, with room for the
    // next interval. No timers, sleeps, or elapsed-time assertions are involved.
    private static readonly DateTime ReportTime = DateTime.MaxValue.AddDays(-1);

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(16 * 1024 - 1, false)]
    [InlineData(16 * 1024 - 1, true)]
    public void ObserverFailurePrecedesMetadataCommitAndSameListRetryDoesNotDuplicate(int existingCount, bool failStatistics)
    {
        var (cache, monitor, _) = CreateCache();
        if (existingCount > 0)
        {
            cache.Add(Enumerable.Range(1, existingCount).Select(CreateMessage).ToList(), DateTime.UnixEpoch);
        }

        monitor.ClearObservations();
        monitor.FailNextTrack = !failStatistics;
        monitor.FailNextReport = failStatistics;
        var incoming = new List<CachedMessage> { CreateMessage(existingCount + 1), CreateMessage(existingCount + 2) };

        Assert.Same(monitor.Failure, Assert.Throws<InvalidOperationException>(() => cache.Add(incoming, ReportTime)));
        Assert.Equal(existingCount, cache.ItemCount);
        Assert.Equal(existingCount == 0, cache.IsEmpty);
        Assert.Equal(existingCount == 0 ? (long?)null : 1, cache.Oldest?.SequenceNumber);
        Assert.Equal(existingCount == 0 ? (long?)null : existingCount, cache.Newest?.SequenceNumber);
        Assert.All(monitor.ObservedCacheCounts, count => Assert.Equal(existingCount, count));

        // Retrying at the identical timestamp also retries a failed periodic report.
        cache.Add(incoming, ReportTime);

        Assert.Equal(existingCount + 2, cache.ItemCount);
        Assert.Equal(2, monitor.TrackCalls);
        Assert.Equal(failStatistics ? 2 : 1, monitor.ReportCalls);
        Assert.All(monitor.ObservedCacheCounts, count => Assert.Equal(existingCount, count));
        var statistics = monitor.LastReport!.Value;
        var oldest = CreateMessage(existingCount == 0 ? existingCount + 1 : 1);
        Assert.Equal(oldest.EnqueueTimeUtc, statistics.OldestEnqueued);
        Assert.Equal(oldest.DequeueTimeUtc, statistics.OldestDequeued);
        Assert.Equal(incoming[^1].EnqueueTimeUtc, statistics.NewestEnqueued);
        Assert.Equal(existingCount + 2, statistics.Count);

        var acquisition = cache.TryGetCursor(Stream, new EventSequenceTokenV2(existingCount + 1));
        Assert.Equal(QueueCacheCursorResultKind.Success, acquisition.Kind);
        var cursor = acquisition.Cursor!;
        ((IQueueCacheCursorProgress)cursor).EnableDeliveryProgress();
        foreach (var expected in incoming)
        {
            Assert.Equal(QueueCacheCursorMoveResultKind.Success, cache.TryGetNextMessageWithResult(cursor, out var batch).Kind);
            Assert.Equal(expected.SequenceNumber, batch!.SequenceToken.SequenceNumber);
        }

        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cache.TryGetNextMessageWithResult(cursor, out _).Kind);
        ((IQueueCacheCursorProgress)cursor).RecordDeliveryCompletion();

        // A successful commit advances the reporting deadline exactly once.
        cache.Add([], ReportTime);
        Assert.Equal(failStatistics ? 2 : 1, monitor.ReportCalls);
        cache.Add([], ReportTime.AddMinutes(1));
        Assert.Equal(failStatistics ? 3 : 2, monitor.ReportCalls);
    }

    [Fact]
    public void NullAdmissionIsRejectedBeforeObserversOrMetadataMutation()
    {
        var (cache, monitor, _) = CreateCache();
        cache.Add([CreateMessage(1)], DateTime.UnixEpoch);
        monitor.ClearObservations();

        var exception = Assert.Throws<ArgumentNullException>(() => cache.Add(null!, ReportTime));

        Assert.Equal("messages", exception.ParamName);
        Assert.Equal(1, cache.ItemCount);
        Assert.Equal(1, cache.Newest?.SequenceNumber);
        Assert.Equal(0, monitor.TrackCalls);
        Assert.Equal(0, monitor.ReportCalls);
    }

    [Fact]
    public void InvalidReportingDeadlineIsRejectedBeforeObserversOrMetadataMutation()
    {
        var (cache, monitor, _) = CreateCache();
        cache.Add([CreateMessage(1)], DateTime.UnixEpoch);
        monitor.ClearObservations();
        var incoming = new List<CachedMessage> { CreateMessage(2) };

        Assert.Throws<ArgumentOutOfRangeException>(() => cache.Add(incoming, DateTime.MaxValue));
        Assert.Equal(1, cache.ItemCount);
        Assert.Equal(0, monitor.TrackCalls);
        Assert.Equal(0, monitor.ReportCalls);

        cache.Add(incoming, ReportTime);
        Assert.Equal(2, cache.ItemCount);
        Assert.Equal(1, monitor.TrackCalls);
        Assert.Equal(1, monitor.ReportCalls);
    }

    [Fact]
    public void MetadataAdmissionAndStatisticsDoNotInvokeTheReadAdapter()
    {
        var (cache, monitor, adapter) = CreateCache();
        adapter.RejectAccess = true;

        cache.Add([CreateMessage(1), CreateMessage(2)], ReportTime);

        Assert.Equal(0, adapter.Calls);
        Assert.Equal(2, cache.ItemCount);
        Assert.Equal(2, monitor.LastReport!.Value.Count);
        Assert.All(monitor.ObservedCacheCounts, count => Assert.Equal(0, count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyAdmissionReportsExistingStatisticsWithoutChangingMetadata(bool hasExistingMessage)
    {
        var (cache, monitor, _) = CreateCache();
        if (hasExistingMessage)
        {
            cache.Add([CreateMessage(1)], DateTime.UnixEpoch);
        }

        monitor.ClearObservations();
        cache.Add([], ReportTime);

        Assert.Equal(hasExistingMessage ? 1 : 0, cache.ItemCount);
        var statistics = monitor.LastReport!.Value;
        Assert.Equal(cache.ItemCount, statistics.Count);
        Assert.Equal(cache.Oldest?.EnqueueTimeUtc, statistics.OldestEnqueued);
        Assert.Equal(cache.Oldest?.DequeueTimeUtc, statistics.OldestDequeued);
        Assert.Equal(cache.Newest?.EnqueueTimeUtc, statistics.NewestEnqueued);
    }

    private static CachedMessage CreateMessage(int number) => new()
    {
        StreamId = Stream,
        SequenceNumber = number,
        EnqueueTimeUtc = DateTime.UnixEpoch.AddSeconds(number),
        DequeueTimeUtc = DateTime.UnixEpoch.AddSeconds(number + 1),
    };

    private static (PooledQueueCache Cache, ThrowingMonitor Monitor, TestAdapter Adapter) CreateCache()
    {
        PooledQueueCache cache = null!;
        var monitor = new ThrowingMonitor(() => cache.ItemCount);
        var adapter = new TestAdapter();
        cache = new PooledQueueCache(adapter, NullLogger.Instance, monitor, TimeSpan.FromMinutes(1));
        return (cache, monitor, adapter);
    }

    private sealed class ThrowingMonitor(Func<int> getItemCount) : ICacheMonitor
    {
        public InvalidOperationException Failure { get; } = new("Transient monitor failure.");
        public bool FailNextTrack { get; set; }
        public bool FailNextReport { get; set; }
        public int TrackCalls { get; private set; }
        public int ReportCalls { get; private set; }
        public List<int> ObservedCacheCounts { get; } = [];
        public (DateTime? OldestEnqueued, DateTime? OldestDequeued, DateTime? NewestEnqueued, long Count)? LastReport { get; private set; }

        public void TrackMessagesAdded(long messagesAdded)
        {
            TrackCalls++;
            ObservedCacheCounts.Add(getItemCount());
            if (FailNextTrack)
            {
                FailNextTrack = false;
                throw Failure;
            }
        }

        public void ReportMessageStatistics(DateTime? oldestMessageEnqueueTimeUtc, DateTime? oldestMessageDequeueTimeUtc,
            DateTime? newestMessageEnqueueTimeUtc, long totalMessageCount)
        {
            ReportCalls++;
            ObservedCacheCounts.Add(getItemCount());
            LastReport = (oldestMessageEnqueueTimeUtc, oldestMessageDequeueTimeUtc, newestMessageEnqueueTimeUtc, totalMessageCount);
            if (FailNextReport)
            {
                FailNextReport = false;
                throw Failure;
            }
        }

        public void ClearObservations()
        {
            TrackCalls = 0;
            ReportCalls = 0;
            LastReport = null;
            ObservedCacheCounts.Clear();
        }

        public void TrackCachePressureMonitorStatusChange(string pressureMonitorType, bool underPressure,
            double? cachePressureContributionCount, double? currentPressure, double? flowControlThreshold)
        { }
        public void TrackMessagesPurged(long messagesPurged) { }
        public void TrackMemoryAllocated(int memoryInBytes) { }
        public void TrackMemoryReleased(int memoryInBytes) { }
        public void ReportCacheSize(long totalCacheSizeInBytes) { }
    }

    private sealed class TestAdapter : ICacheDataAdapter
    {
        public bool RejectAccess { get; set; }
        public int Calls { get; private set; }
        private void OnAccess()
        {
            Calls++;
            if (RejectAccess) throw new InvalidOperationException("The read adapter must not run during admission.");
        }

        public IBatchContainer GetBatchContainer(ref CachedMessage message)
        {
            OnAccess();
            return new TestBatch(message.StreamId, new EventSequenceTokenV2(message.SequenceNumber));
        }

        public StreamSequenceToken GetSequenceToken(ref CachedMessage message)
        {
            OnAccess();
            return new EventSequenceTokenV2(message.SequenceNumber);
        }

        public int Compare(ref CachedMessage message, StreamSequenceToken token)
        {
            OnAccess();
            return message.SequenceNumber.CompareTo(token.SequenceNumber);
        }
    }

    private sealed class TestBatch(StreamId streamId, StreamSequenceToken token) : IBatchContainer
    {
        public StreamId StreamId => streamId;
        public StreamSequenceToken SequenceToken => token;
        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => [];
        public bool ImportRequestContext() => false;
    }
}

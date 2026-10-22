using System.Net;
using Orleans.DurableJobs.Diagnostics;
using Orleans.Journaling;
using Orleans.Runtime;
using Xunit;

namespace Tester.DurableJobs;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableJobs")]
[TestCategory("BVT"), TestCategory("DurableJobs")]
public sealed class DurableJobsEventsTests
{
    [Fact]
    public void AllEventsReportsTypedShardOpenTransitions()
    {
        var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 1);
        var journal = new JournalId("jobs/shards/diagnostic-test");
        var observer = new RecordingObserver();
        using var subscription = DurableJobsEvents.AllEvents.Subscribe(observer);

        DurableJobsEvents.EmitShardOpenJoined(silo, journal);
        DurableJobsEvents.EmitShardOpenRetryAfterCancellation(silo, journal);

        Assert.Collection(
            observer.Events,
            item =>
            {
                var joined = Assert.IsType<DurableJobsEvents.ShardOpenJoined>(item);
                Assert.Equal(silo, joined.SiloAddress);
                Assert.Equal(journal, joined.JournalId);
            },
            item =>
            {
                var retry = Assert.IsType<DurableJobsEvents.ShardOpenRetryAfterCancellation>(item);
                Assert.Equal(silo, retry.SiloAddress);
                Assert.Equal(journal, retry.JournalId);
            });
    }

    [Fact]
    public void DisposingObserverStopsShardOpenNotifications()
    {
        var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 1);
        var journal = new JournalId("jobs/shards/disposed-observer");
        var observer = new RecordingObserver();
        using var subscription = DurableJobsEvents.AllEvents.Subscribe(observer);
        DurableJobsEvents.EmitShardOpenJoined(silo, journal);
        subscription.Dispose();

        DurableJobsEvents.EmitShardOpenRetryAfterCancellation(silo, journal);

        Assert.IsType<DurableJobsEvents.ShardOpenJoined>(Assert.Single(observer.Events));
    }

    private sealed class RecordingObserver : IObserver<DurableJobsEvents.DurableJobEvent>
    {
        public List<DurableJobsEvents.DurableJobEvent> Events { get; } = [];

        public void OnCompleted() { }
        public void OnError(Exception error) => throw error;
        public void OnNext(DurableJobsEvents.DurableJobEvent value) => Events.Add(value);
    }
}

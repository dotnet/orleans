using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans.Diagnostics;
using Orleans.DurableJobs;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests;

/// <summary>
/// Verifies that scheduling, executing, handling, and persisting a durable job all share the
/// W3C trace context of the originating caller activity. These tests prove the end-to-end trace
/// continuity that powers the Aspire dashboard view for durable jobs.
/// </summary>
public class DurableJobsTracingTests : IClassFixture<DurableJobsTracingTests.Fixture>
{
    private static readonly ConcurrentBag<Activity> CapturedActivities = new();
    private static readonly ActivityListener Listener;
    private static readonly ActivitySource TestSource = new("DurableJobsTracingTests");

    static DurableJobsTracingTests()
    {
        Listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == ActivitySources.DurableJobsActivitySourceName
                || source.Name == ActivitySources.ApplicationGrainActivitySourceName
                || source.Name == ActivitySources.RuntimeActivitySourceName
                || source.Name == ActivitySources.LifecycleActivitySourceName
                || source.Name == ActivitySources.StorageActivitySourceName
                || source.Name == TestSource.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                var _ = options.TraceId;
                return ActivitySamplingResult.AllDataAndRecorded;
            },
            SampleUsingParentId = static (ref ActivityCreationOptions<string> options) =>
            {
                var _ = options.TraceId;
                return ActivitySamplingResult.AllDataAndRecorded;
            },
            ActivityStopped = static activity => CapturedActivities.Add(activity)
        };

        ActivitySource.AddActivityListener(Listener);
    }

    /// <summary>
    /// Cluster fixture that mirrors <see cref="DefaultClusterFixture"/> but additionally turns on
    /// activity propagation so the schedule-time trace flows across the receiver grain RPC.
    /// </summary>
    public class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            builder.AddClientBuilderConfigurator<ClientConfigurator>();
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder) =>
                hostBuilder
                    .AddActivityPropagation()
                    .UseInMemoryReminderService()
                    .UseInMemoryDurableJobs()
                    .AddMemoryGrainStorageAsDefault()
                    .AddMemoryGrainStorage("MemoryStore");
        }

        private class ClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) =>
                clientBuilder.AddActivityPropagation();
        }
    }

    private readonly Fixture _fixture;

    public DurableJobsTracingTests(Fixture fixture)
    {
        _fixture = fixture;
        CapturedActivities.Clear();
    }

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public async Task ScheduleAndExecuteShareTraceId()
    {
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;

        var grain = _fixture.GrainFactory.GetGrain<IDurableJobGrain>(Guid.NewGuid().ToString());

        string expectedTraceId;
        DurableJob job;
        using (var parentActivity = TestSource.StartActivity("durablejobs.tracing.parent", ActivityKind.Internal))
        {
            Assert.NotNull(parentActivity);
            expectedTraceId = parentActivity.TraceId.ToString();

            job = await grain.ScheduleJobAsync(
                jobName: "tracing-test-job",
                scheduledTime: DateTimeOffset.UtcNow.AddMilliseconds(250));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await grain.WaitForJobToRun(job.Id).WaitAsync(cts.Token);

        Assert.False(string.IsNullOrEmpty(job.TraceParent), "TraceParent should be persisted on the scheduled job.");
        Assert.Contains(expectedTraceId, job.TraceParent);

        var handlerTraceId = await grain.GetJobTraceId(job.Id);
        Assert.Equal(expectedTraceId, handlerTraceId);

        // Allow the post-execute follow-up storage write (RemoveJob) to settle before snapshotting.
        await Task.Delay(250);

        var traceActivities = CapturedActivities
            .Where(a => a.TraceId.ToString() == expectedTraceId)
            .ToArray();

        Assert.Contains(traceActivities, a => a.Source.Name == ActivitySources.DurableJobsActivitySourceName
            && a.OperationName == "schedule durable job");
        Assert.Contains(traceActivities, a => a.Source.Name == ActivitySources.DurableJobsActivitySourceName
            && a.OperationName == "execute durable job");
        Assert.Contains(traceActivities, a => a.Source.Name == ActivitySources.DurableJobsActivitySourceName
            && a.OperationName == "execute durable job handler");
        Assert.Contains(traceActivities, a => a.Source.Name == ActivitySources.StorageActivitySourceName
            && a.OperationName.StartsWith("journal ", StringComparison.Ordinal));
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Tester.DurableJobs;
using TestExtensions;
using Xunit;

namespace DefaultCluster.Tests;

public class InMemoryDurableJobsTests : HostedTestClusterEnsureDefaultStarted
{
    private readonly DurableJobTestsRunner _runner;

    public InMemoryDurableJobsTests(DefaultClusterFixture fixture) : base(fixture)
    {
        _runner = new DurableJobTestsRunner(this.GrainFactory);
    }

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public async Task DurableJobGrain()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.DurableJobGrain(cts.Token);
    }

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public async Task JobExecutionOrder()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.JobExecutionOrder(cts.Token);
    }

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public async Task PastDueTime()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.PastDueTime(cts.Token);
    }

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public async Task JobWithMetadata()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.JobWithMetadata(cts.Token);
    }

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public async Task MultipleGrains()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.MultipleGrains(cts.Token);
    }

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public async Task DuplicateJobNames()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.DuplicateJobNames(cts.Token);
    }

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public async Task CancelNonExistentJob()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.CancelNonExistentJob(cts.Token);
    }

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public async Task CancelAlreadyExecutedJob()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.CancelAlreadyExecutedJob(cts.Token);
    }

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public async Task ConcurrentScheduling()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ConcurrentScheduling(cts.Token);
    }

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public async Task JobPropertiesVerification()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.JobPropertiesVerification(cts.Token);
    }

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public async Task DequeueCount()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.DequeueCount(cts.Token);
    }

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public async Task ScheduleJobOnAnotherGrain()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ScheduleJobOnAnotherGrain(cts.Token);
    }

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public async Task JobRetry()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.JobRetry(cts.Token);
    }
}

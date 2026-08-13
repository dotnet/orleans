using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Serialization.Invocation;
using Tester.DurableJobs;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
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

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class DurableJobsResponseTimeoutTests
{
    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public void JobRetryWaitOverridesClientResponseTimeout()
    {
        var request = typeof(IRetryTestGrain).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract
                && !type.ContainsGenericParameters
                && typeof(IInvokable).IsAssignableFrom(type))
            .Select(type => (IInvokable)Activator.CreateInstance(type)!)
            .Single(request => request.GetInterfaceType() == typeof(IRetryTestGrain)
                && request.GetMethodName() == nameof(IRetryTestGrain.WaitForJobToSucceed));

        Assert.Equal(TimeSpan.FromMinutes(2), request.GetDefaultResponseTimeout());
    }
}

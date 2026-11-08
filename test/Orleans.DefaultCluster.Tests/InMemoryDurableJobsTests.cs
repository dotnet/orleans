using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Serialization.Invocation;
using Tester.DurableJobs;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
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
    public async Task DurableJobOwnerTurnSerializesWritesAndRetainsAttemptAcrossSteps()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var grain = GrainFactory.GetGrain<IDurableJobOwnerTurnGrain>(Guid.NewGuid());
        await grain.ScheduleAsync().WaitAsync(cts.Token);
        await grain.WaitForFirstStepAsync().WaitAsync(cts.Token);
        Task write;
        try
        {
            Assert.True(HostedCluster.TryGetGrainContext(grain.GetGrainId(), out var context));
            var owner = Assert.IsType<DurableJobOwnerTurnGrain>(context.GrainInstance);
            write = grain.WriteAsync();
            await owner.WriteQueued.WaitAsync(cts.Token);
            Assert.False(write.IsCompleted);
        }
        finally
        {
            await grain.ReleaseFirstStepAsync().WaitAsync(cts.Token);
        }

        await write.WaitAsync(cts.Token);
        await grain.WaitForCompletionEffectAsync().WaitAsync(cts.Token);
        try
        {
            // The post-commit effect can await another owner which reads this grain.
            await grain.WriteAsync().WaitAsync(cts.Token);
        }
        finally
        {
            await grain.ReleaseCompletionEffectAsync().WaitAsync(cts.Token);
        }
        Assert.Equal(2, await grain.WaitForCompletionAsync().WaitAsync(cts.Token));
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

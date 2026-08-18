using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using Xunit;

namespace Tester.Scheduler;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public class WorkItemGroupWaiterTests
{
    [Fact]
    public async Task WaitCompletesAfterSignal()
    {
        var waiter = new WorkItemGroupWaiter(null!);
        var wait = waiter.WaitAsync();

        Assert.False(wait.IsCompleted);
        waiter.Signal();
        Assert.True(wait.IsCompleted);
        await wait;
    }

    [Fact]
    public void SignalBeforeWaitCompletesSynchronously()
    {
        var waiter = new WorkItemGroupWaiter(null!);

        waiter.Signal();
        var wait = waiter.WaitAsync();

        Assert.True(wait.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ConcurrentSignalsReleaseSingleWaiter()
    {
        var waiter = new WorkItemGroupWaiter(null!);
        var wait = waiter.WaitAsync();

        Parallel.For(0, Environment.ProcessorCount * 4, _ => waiter.Signal());

        Assert.True(wait.IsCompleted);
        await wait;
    }

    [Fact]
    public async Task PendingContinuationRunsThroughWorkItemGroup()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddMetrics()
            .AddSingleton<OrleansInstruments>()
            .AddSingleton<SchedulerInstruments>()
            .BuildServiceProvider();
        var context = Substitute.For<IGrainContext>();
        context.ActivationServices.Returns(services);
        var workItemGroup = new WorkItemGroup(
            context,
            Options.Create(new SchedulingOptions()),
            services.GetRequiredService<SchedulerInstruments>());
        var waiter = new WorkItemGroupWaiter(workItemGroup);
        var wait = waiter.WaitAsync().AsTask();

        waiter.Signal();

        await wait.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

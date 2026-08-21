using System.Reflection;
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
    public async Task SignalRacePublishesCompletionOnce()
    {
        using var services = CreateServices();
        var waiter = new WorkItemGroupWaiter(CreateWorkItemGroup(services));

        await RunSignalRace().WaitAsync(TimeSpan.FromSeconds(10));

        async Task RunSignalRace()
        {
            for (var i = 0; i < 1_000; i++)
            {
                var wait = waiter.WaitAsync();
                var signal = Task.Run(waiter.Signal);
                await wait;
                await signal;
            }
        }
    }

    [Fact]
    public async Task PendingContinuationRunsThroughWorkItemGroup()
    {
        using var services = CreateServices();
        var workItemGroup = CreateWorkItemGroup(services);
        var waiter = new WorkItemGroupWaiter(workItemGroup);
        var wait = waiter.WaitAsync().AsTask();

        waiter.Signal();

        await wait.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task DirectCallbacksRestoreExecutionContext()
    {
        using var services = CreateServices();
        var workItemGroup = CreateWorkItemGroup(services);
        var asyncLocal = new AsyncLocal<object?>();
        var observedValue = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        workItemGroup.QueueAction(() => asyncLocal.Value = new object());
        workItemGroup.QueueAction(() => observedValue.SetResult(asyncLocal.Value));

        Assert.Null(await observedValue.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void SchedulerTaskDeniesChildAttachment()
    {
        using var services = CreateServices();
        var workItemGroup = CreateWorkItemGroup(services);
        var field = typeof(WorkItemGroup).GetField("_schedulerTask", BindingFlags.Instance | BindingFlags.NonPublic);
        var schedulerTask = Assert.IsType<Task>(field!.GetValue(workItemGroup));

        Assert.True(schedulerTask.CreationOptions.HasFlag(TaskCreationOptions.DenyChildAttach));
    }

    private static ServiceProvider CreateServices() => new ServiceCollection()
        .AddLogging()
        .AddMetrics()
        .AddSingleton<OrleansInstruments>()
        .AddSingleton<SchedulerInstruments>()
        .BuildServiceProvider();

    private static WorkItemGroup CreateWorkItemGroup(ServiceProvider services)
    {
        var context = Substitute.For<IGrainContext>();
        context.ActivationServices.Returns(services);
        return new WorkItemGroup(
            context,
            Options.Create(new SchedulingOptions()),
            services.GetRequiredService<SchedulerInstruments>());
    }
}

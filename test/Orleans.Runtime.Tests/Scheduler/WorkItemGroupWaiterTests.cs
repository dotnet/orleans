using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        using var services = CreateServices();
        var workItemGroup = CreateWorkItemGroup(services);
        SetRunning(workItemGroup);
        var waiter = new WorkItemGroupWaiter(workItemGroup);
        var wait = waiter.WaitAsync().AsTask();

        Parallel.For(0, Environment.ProcessorCount * 4, _ => waiter.Signal());

        Assert.False(wait.IsCompleted);
        workItemGroup.Execute();
        await wait;
    }

    [Fact]
    public void ConcurrentWaitersAreRejected()
    {
        var waiter = new WorkItemGroupWaiter(null!);
        _ = waiter.WaitAsync();

        Assert.Throws<InvalidOperationException>(() => waiter.WaitAsync());
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
        SetRunning(workItemGroup);
        var waiter = new WorkItemGroupWaiter(workItemGroup);
        var wait = ObserveScheduler(waiter.WaitAsync());

        waiter.Signal();
        Assert.False(wait.IsCompleted);
        workItemGroup.Execute();

        Assert.Same(workItemGroup.TaskScheduler, await wait.WaitAsync(TimeSpan.FromSeconds(10)));

        static async Task<TaskScheduler> ObserveScheduler(ValueTask wait)
        {
            await wait;
            return TaskScheduler.Current;
        }
    }

    [Fact]
    public async Task ReuseInvalidatesPriorValueTask()
    {
        var waiter = new WorkItemGroupWaiter(null!);
        var firstWait = waiter.WaitAsync();
        waiter.Signal();
        await firstWait;

        var secondWait = waiter.WaitAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await firstWait);

        waiter.Signal();
        await secondWait;
    }

    [Fact]
    public async Task GetResultBeforeCompletionPreservesPendingWait()
    {
        var waiter = new WorkItemGroupWaiter(null!);
        var wait = waiter.WaitAsync();

        Assert.Throws<InvalidOperationException>(() => wait.GetAwaiter().GetResult());

        waiter.Signal();
        await wait;
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
    public void DirectCallbacksRunWithSuppressedExecutionContextFlow()
    {
        using var services = CreateServices();
        var workItemGroup = CreateWorkItemGroup(
            services,
            new SchedulingOptions { ActivationSchedulingQuantum = TimeSpan.Zero });
        SetRunning(workItemGroup);
        var asyncLocal = new AsyncLocal<object?>();
        object? observedValue = new object();

        workItemGroup.QueueAction(() => asyncLocal.Value = new object());
        workItemGroup.QueueAction(() => observedValue = asyncLocal.Value);

        using (ExecutionContext.SuppressFlow())
        {
            workItemGroup.Execute();
        }

        Assert.Null(observedValue);
    }

    [Fact]
    public void SchedulerTaskDeniesChildAttachment()
    {
        using var services = CreateServices();
        var workItemGroup = CreateWorkItemGroup(services);
        var schedulerTask = GetSchedulerTask(workItemGroup);

        Assert.True(schedulerTask.CreationOptions.HasFlag(TaskCreationOptions.DenyChildAttach));
    }

    [Fact]
    public void SchedulerTaskDoesNotCaptureExecutionContext()
    {
        using var services = CreateServices();
        var asyncLocal = new AsyncLocal<object?> { Value = new object() };
        var workItemGroup = CreateWorkItemGroup(services);
        var schedulerTask = GetSchedulerTask(workItemGroup);
        var contingentPropertiesField = typeof(Task).GetField("m_contingentProperties", BindingFlags.Instance | BindingFlags.NonPublic);
        var contingentProperties = contingentPropertiesField!.GetValue(schedulerTask);

        if (contingentProperties is not null)
        {
            var capturedContextField = contingentProperties.GetType().GetField("m_capturedContext", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.Null(capturedContextField!.GetValue(contingentProperties));
        }

        GC.KeepAlive(asyncLocal);
    }

    [Fact]
    public void DirectCallbackExceptionsAreLoggedAndDoNotInterruptQueueDrain()
    {
        var logger = Substitute.For<ILogger<WorkItemGroup>>();
        logger.IsEnabled(LogLevel.Error).Returns(true);
        using var services = CreateServices(logger);
        var workItemGroup = CreateWorkItemGroup(
            services,
            new SchedulingOptions { ActivationSchedulingQuantum = TimeSpan.Zero });
        SetRunning(workItemGroup);
        var executingThread = Environment.CurrentManagedThreadId;
        var subsequentCallbackRanOnExecutingThread = false;

        workItemGroup.QueueAction(static () => throw new InvalidOperationException("Test exception"));
        workItemGroup.QueueAction(() => subsequentCallbackRanOnExecutingThread = Environment.CurrentManagedThreadId == executingThread);
        workItemGroup.Execute();

        Assert.True(subsequentCallbackRanOnExecutingThread);
        Assert.Contains(
            logger.ReceivedCalls(),
            call => call.GetMethodInfo().Name == nameof(ILogger.Log)
                && (LogLevel)call.GetArguments()[0]! == LogLevel.Error
                && call.GetArguments()[3] is InvalidOperationException);
    }

    [Fact]
    public void CallbackQueuedDuringDrainRunsInSameExecution()
    {
        using var services = CreateServices();
        var workItemGroup = CreateWorkItemGroup(
            services,
            new SchedulingOptions { ActivationSchedulingQuantum = TimeSpan.Zero });
        SetRunning(workItemGroup);
        var callbacks = new List<int>();

        workItemGroup.QueueAction(() =>
        {
            callbacks.Add(1);
            workItemGroup.QueueAction(() => callbacks.Add(2));
        });

        workItemGroup.Execute();

        Assert.Equal([1, 2], callbacks);
        Assert.Equal(0, workItemGroup.ExternalWorkItemCount);
    }

    [Fact]
    public void FaultedTaskDoesNotInterruptQueueDrain()
    {
        using var services = CreateServices();
        var workItemGroup = CreateWorkItemGroup(
            services,
            new SchedulingOptions { ActivationSchedulingQuantum = TimeSpan.Zero });
        SetRunning(workItemGroup);
        var task = new Task(static () => throw new InvalidOperationException("Test exception"));
        var subsequentCallbackRan = false;

        task.Start(workItemGroup.TaskScheduler);
        workItemGroup.QueueAction(() => subsequentCallbackRan = true);
        workItemGroup.Execute();

        Assert.True(task.IsFaulted);
        Assert.True(subsequentCallbackRan);
    }

    [Fact]
    public void QueueActionRejectsNullCallbacksSynchronously()
    {
        using var services = CreateServices();
        var workItemGroup = CreateWorkItemGroup(services);
        SetRunning(workItemGroup);

        Assert.Throws<ArgumentNullException>("action", () => workItemGroup.QueueAction((Action)null!));
        Assert.Throws<ArgumentNullException>("action", () => workItemGroup.QueueAction((Action<object>)null!, new object()));
        Assert.Equal(0, workItemGroup.ExternalWorkItemCount);
    }

    [Fact]
    public void PostRejectsNullCallbackSynchronously()
    {
        using var services = CreateServices();
        var workItemGroup = CreateWorkItemGroup(services);
        SetRunning(workItemGroup);

        Assert.Throws<ArgumentNullException>("d", () => workItemGroup.Post(null!, null));
        Assert.Equal(0, workItemGroup.ExternalWorkItemCount);
    }

    private static ServiceProvider CreateServices(ILogger<WorkItemGroup>? logger = null)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddMetrics()
            .AddSingleton<OrleansInstruments>()
            .AddSingleton<SchedulerInstruments>();

        if (logger is not null)
        {
            services.AddSingleton(logger);
        }

        return services.BuildServiceProvider();
    }

    private static WorkItemGroup CreateWorkItemGroup(ServiceProvider services, SchedulingOptions? schedulingOptions = null)
    {
        var context = Substitute.For<IGrainContext>();
        context.ActivationServices.Returns(services);
        return new WorkItemGroup(
            context,
            Options.Create(schedulingOptions ?? new SchedulingOptions()),
            services.GetRequiredService<SchedulerInstruments>());
    }

    private static void SetRunning(WorkItemGroup workItemGroup)
    {
        var stateField = typeof(WorkItemGroup).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic);
        stateField!.SetValue(workItemGroup, Enum.Parse(stateField.FieldType, "Running"));
    }

    private static Task GetSchedulerTask(WorkItemGroup workItemGroup)
    {
        var field = typeof(WorkItemGroup).GetField("_schedulerTask", BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<Task>(field!.GetValue(workItemGroup));
    }
}

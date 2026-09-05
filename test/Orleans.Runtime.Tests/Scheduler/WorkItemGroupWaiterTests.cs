using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
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

        await RunSignalRace().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

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

        Assert.Same(
            workItemGroup.TaskScheduler,
            await wait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

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
    public async Task DuplicateSignalsAreCoalesced()
    {
        var waiter = new WorkItemGroupWaiter(null!);
        var firstWait = waiter.WaitAsync();

        Parallel.For(0, Environment.ProcessorCount * 4, _ => waiter.Signal());
        await firstWait;

        var secondWait = waiter.WaitAsync();
        Assert.False(secondWait.IsCompleted);

        waiter.Signal();
        await secondWait;
    }

    [Fact]
    public async Task ReuseRemainsOperationalAcrossTokenRollover()
    {
        var waiter = new WorkItemGroupWaiter(null!);
        ValueTask previousWait = default;

        for (var i = 0; i <= ushort.MaxValue; i++)
        {
            previousWait = waiter.WaitAsync();
            waiter.Signal();
            Assert.True(previousWait.IsCompletedSuccessfully);
            await previousWait;
        }

        var currentWait = waiter.WaitAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await previousWait);

        waiter.Signal();
        await currentWait;
    }

    [Fact]
    public async Task SignalAndContinuationRegistrationRaceCompletesExactlyOnce()
    {
        const int IterationCount = 2_000;
        using var services = CreateServices();
        var workItemGroup = CreateWorkItemGroup(
            services,
            new SchedulingOptions { ActivationSchedulingQuantum = TimeSpan.Zero });
        var waiter = new WorkItemGroupWaiter(workItemGroup);
        var callbackCount = 0;

        for (var i = 0; i < IterationCount; i++)
        {
            SetRunning(workItemGroup);
            var wait = waiter.WaitAsync();
            var awaiter = wait.GetAwaiter();
            var register = Task.Run(
                () => awaiter.UnsafeOnCompleted(() => Interlocked.Increment(ref callbackCount)),
                TestContext.Current.CancellationToken);
            var signal = Task.Run(waiter.Signal, TestContext.Current.CancellationToken);

            await Task.WhenAll(register, signal);
            workItemGroup.Execute();
            await wait;
        }

        Assert.Equal(IterationCount, callbackCount);
    }

    [Fact]
    public async Task ContinuationRegisteredAfterSignalIsQueued()
    {
        using var services = CreateServices();
        var workItemGroup = CreateWorkItemGroup(services);
        SetRunning(workItemGroup);
        var waiter = new WorkItemGroupWaiter(workItemGroup);
        var wait = waiter.WaitAsync();
        var awaiter = wait.GetAwaiter();
        TaskScheduler? observedScheduler = null;

        waiter.Signal();
        Assert.True(awaiter.IsCompleted);
        awaiter.UnsafeOnCompleted(() => observedScheduler = TaskScheduler.Current);

        Assert.Null(observedScheduler);
        workItemGroup.Execute();

        Assert.Same(workItemGroup.TaskScheduler, observedScheduler);
        await wait;
    }

    [Fact]
    public void CompletedWaitDoesNotRetainContinuationState()
    {
        var weakReference = CompleteWaitWithState();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weakReference.IsAlive);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference CompleteWaitWithState()
        {
            using var services = CreateServices();
            var workItemGroup = CreateWorkItemGroup(services);
            SetRunning(workItemGroup);
            var waiter = new WorkItemGroupWaiter(workItemGroup);
            var source = (IValueTaskSource)waiter;
            var state = new object();
            var weakReference = new WeakReference(state);

            _ = waiter.WaitAsync();
            source.OnCompleted(static _ => { }, state, token: 0, ValueTaskSourceOnCompletedFlags.None);
            waiter.Signal();
            workItemGroup.Execute();
#pragma warning disable xUnit1031 // The custom value-task source is known to be complete.
            source.GetResult(token: 0);
#pragma warning restore xUnit1031

            return weakReference;
        }
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

        Assert.Null(await observedValue.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
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
    public void DirectCallbacksRunWithActivationTaskScheduler()
    {
        using var services = CreateServices();
        var workItemGroup = CreateWorkItemGroup(services);
        SetRunning(workItemGroup);
        TaskScheduler? observedScheduler = null;

        workItemGroup.QueueAction(() => observedScheduler = TaskScheduler.Current);
        workItemGroup.Execute();

        Assert.Same(workItemGroup.TaskScheduler, observedScheduler);
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
        var (workItemGroup, ambientValue) = CreateWorkItemGroupWithAmbientValue(services);

        for (var i = 0; ambientValue.IsAlive && i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(ambientValue.IsAlive);
        GC.KeepAlive(workItemGroup);
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
    public void QueueNullableActionPreservesNullState()
    {
        using var services = CreateServices();
        var workItemGroup = CreateWorkItemGroup(services);
        SetRunning(workItemGroup);
        object? observedState = new object();

        workItemGroup.QueueNullableAction(state => observedState = state, null);
        workItemGroup.Execute();

        Assert.Null(observedState);
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WorkItemGroup WorkItemGroup, WeakReference AmbientValue) CreateWorkItemGroupWithAmbientValue(ServiceProvider services)
    {
        var asyncLocal = new AsyncLocal<object?> { Value = new object() };
        var ambientValue = new WeakReference(asyncLocal.Value);
        var workItemGroup = CreateWorkItemGroup(services);
        asyncLocal.Value = null;
        return (workItemGroup, ambientValue);
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

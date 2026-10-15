using System.Collections.Concurrent;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Diagnostics;
using Orleans.DurableTasks.Runtime;
using Orleans.Messaging;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

public sealed partial class DurableTaskRuntimeInvariantTests
{
    [Fact]
    public Task CommittedBodyResultLeavesNoPendingWorkWhenTheAttemptCommitsItAgain() => ActivationScheduler.RunAsync(async () =>
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (runtime, _, manager, _) = CreateRuntime();
        var workSource = (IInboxWorkSource)runtime;
        var rootId = TaskId.Parse("root");
        var scheduler = TaskScheduler.Current;
        Task? interleavedWrite = null;

        // The body's final code stages its result, and a write which another operation requests commits it before the
        // attempt commits the result again. That write captured every change, so the attempt's write carries none.
        using var subscription = DurableTaskEvents.AllEvents.Subscribe(new EventObserver(evt =>
        {
            if (evt is DurableTaskEvents.TaskCompleted { TaskId: var taskId } && taskId == rootId && interleavedWrite is null)
            {
                interleavedWrite = Task.Factory.StartNew(
                    async () =>
                    {
                        await manager.WriteStateAsync(cancellationToken);
                        manager.WritesAreEmpty = true;
                    },
                    cancellationToken,
                    TaskCreationOptions.None,
                    scheduler).Unwrap();
            }
        }));
        runtime.ScheduleFromInbox(rootId, CreateRequest(1, () => DurableTask.Run(static async _ =>
        {
            await Task.Yield();
            return 5;
        })));
        await manager.WriteStateAsync(cancellationToken);

        Assert.Equal(5, (await WaitForResponseAsync(runtime, rootId)).GetResult<int>());
        await interleavedWrite!.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        for (var yields = 0; workSource.GetWakeupDelay() is not null; yields++)
        {
            Assert.True(yields < 100, "The committed result stayed pending work.");
            await Task.Yield();
        }

        await runtime.StopAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    });

    [Fact]
    public Task RuntimeReportsATaskTreesLifecycleInOrder() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime(TimeSpan.FromDays(1));
        using var events = new RuntimeEventRecorder(runtime);
        var rootId = TaskId.Parse("root");
        var request = CreateRequest(1, () => DurableTask.Run(async _ => await DurableTask.Run(static _ => 2).WithId("step")));

        runtime.ScheduleFromInbox(rootId, request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => transport.Completions.Count == 1);
        await WaitUntilIdleAsync(runtime);
        ((IOutboxMessageSource)runtime).OnRetired(Assert.Single(transport.Completions).MessageId, OutboxMessageRetirement.Delivered());

        // Each event carries the runtime's activation, and the delivered completion deletes the tree.
        Assert.Equal(
            [
                "TaskCreated root",
                "ExecutionStarted root",
                "TaskCreated root/step",
                "ExecutionStarted root/step",
                "TaskCompleted root/step",
                "ExecutionStopped root/step",
                "TaskCompleted root",
                "ExecutionStopped root",
                "TaskDeleted root/step",
                "TaskDeleted root",
            ],
            events.Events);
        Assert.False(storage.Contains(rootId));
    });

    [Fact]
    public Task ReplayReportsWhenItRunsLive() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var request = CreateRequest(
            1,
            () => DurableTask.Run(async _ =>
            {
                await DurableTask.Run(static _ => 2).WithId("step");
                await DurableTask.Delay(TimeSpan.FromDays(1)).WithId("delay");
            }));

        runtime.ScheduleFromInbox(rootId, request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        // The delay's resume is staged with its due time, so the replacement replays a scheduled delay. A delay child whose
        // body hasn't scheduled it yet is a pending execution, which recovery starts on its own.
        await transport.ResumeSent.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The replacement replays the recorded step and delay; reaching the delay, the last recorded operation, ends the
        // replay without staging anything. The delay's attempt resumes waiting for its due time and ends.
        var (replacement, _, _, _) = CreateRuntime(storage, manager, initialize: false);
        using var events = new RuntimeEventRecorder(replacement);
        replacement.InitializeForActivation();
        await replacement.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => events.Events.Contains("ExecutionStopped root/delay"));

        Assert.Equal(["ExecutionStarted root", "ReplayEnded root", "ExecutionStarted root/delay", "ExecutionStopped root/delay"], events.Events);
        await replacement.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    });

    /// <summary>Records one runtime's events, in the order which its activation raised them.</summary>
    private sealed class RuntimeEventRecorder : IDisposable
    {
        private readonly ConcurrentQueue<string> _events = new();
        private readonly IDisposable _subscription;

        public RuntimeEventRecorder(DurableTaskGrainRuntime runtime)
        {
            var context = runtime.GrainContext;
            _subscription = DurableTaskEvents.AllEvents.Subscribe(new EventObserver(evt =>
            {
                if (ReferenceEquals(evt.GrainContext, context))
                {
                    _events.Enqueue($"{evt.GetType().Name} {evt.TaskId}");
                }
            }));
        }

        public IReadOnlyCollection<string> Events => _events;

        public void Dispose() => _subscription.Dispose();
    }
}

/// <summary>Runs an action for each durable task runtime event, synchronously at the transition which raises it.</summary>
internal sealed class EventObserver(Action<DurableTaskEvents.DurableTaskEvent> onNext) : IObserver<DurableTaskEvents.DurableTaskEvent>
{
    public void OnNext(DurableTaskEvents.DurableTaskEvent value) => onNext(value);

    public void OnCompleted() { }

    public void OnError(Exception error) { }
}

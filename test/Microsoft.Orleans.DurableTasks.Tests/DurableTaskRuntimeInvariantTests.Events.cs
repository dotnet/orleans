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
        var (runtime, storage, manager, _) = CreateRuntime();
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
        await WaitUntilAsync(() => storage.Contains(TaskId.Parse("root/delay")));
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The replacement replays the recorded step and delay; reaching the delay, the last recorded operation, ends the
        // replay without staging anything, and the delay resumes waiting.
        var (replacement, _, _, _) = CreateRuntime(storage, manager, initialize: false);
        using var events = new RuntimeEventRecorder(replacement);
        replacement.InitializeForActivation();
        await replacement.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => events.Events.Contains("ExecutionStarted root/delay"));

        Assert.Equal(["ExecutionStarted root", "ReplayEnded root", "ExecutionStarted root/delay"], events.Events);
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

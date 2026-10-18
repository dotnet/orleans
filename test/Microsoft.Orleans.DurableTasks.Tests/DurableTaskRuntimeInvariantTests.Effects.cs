using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Messaging;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

public sealed partial class DurableTaskRuntimeInvariantTests
{
    [Fact]
    public Task EffectRecordIsATerminalTreeEntryDeletedWithItsExecutionTree() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime(TimeSpan.FromDays(1));
        var taskId = TaskId.Parse("root");
        var effectId = TaskId.Parse("root/reference");
        var reference = Guid.NewGuid();
        var request = CreateRequest(
            1,
            () => DurableTask.Run(_ => DurableTask.RunInline("reference", () => reference)));

        runtime.ScheduleFromInbox(taskId, request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => transport.Completions.Count == 1);
        await WaitUntilIdleAsync(runtime);

        var effect = storage.Get(effectId);
        Assert.Equal("$effect", effect.RequestFingerprint);
        Assert.Equal(reference, effect.Result!.GetResult<Guid>());
        Assert.Null(effect.Request);
        Assert.True(effect.RemoteTarget.IsDefault);
        Assert.Equal(reference, storage.Get(taskId).Result!.GetResult<Guid>());

        // Delivering the task's completion deletes its tree, which holds the effect's record.
        ((IOutboxMessageSource)runtime).OnRetired(Assert.Single(transport.Completions).MessageId, OutboxMessageRetirement.Delivered());

        Assert.False(storage.Contains(taskId));
        Assert.False(storage.Contains(effectId));
    });

    [Fact]
    public Task CallUnderAnEffectsIdentifierDiverges() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        ((IDurableTaskGrainStorage)storage).AddRecord(TaskId.Parse("root/local/charge"), "$effect", DurableTaskResponse.FromResult(1));

        var handle = await runtime.ScheduleChildAsync(
            TaskId.Parse("root/local"),
            DurableTask.Run(async () => await CreateRemoteChild(1).WithId("charge")),
            TestContext.Current.CancellationToken);
        var response = await handle.WaitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var exception = Assert.IsType<InvalidOperationException>(response.Exception);
        Assert.Contains("It records a DurableTask.RunInline effect, and this attempt requested a call to grain", exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Invocations);
    });

    [Fact]
    public Task DelayUnderAnEffectsIdentifierDiverges() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        ((IDurableTaskGrainStorage)storage).AddRecord(TaskId.Parse("root/local/wait"), "$effect", DurableTaskResponse.FromResult(1));

        var handle = await runtime.ScheduleChildAsync(
            TaskId.Parse("root/local"),
            DurableTask.Run(async () => await DurableTask.Delay(TimeSpan.FromDays(1)).WithId("wait")),
            TestContext.Current.CancellationToken);
        var response = await handle.WaitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var exception = Assert.IsType<InvalidOperationException>(response.Exception);
        Assert.Contains("It records a DurableTask.RunInline effect", exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Resumes);
    });
}

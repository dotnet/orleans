using Orleans.DurableTasks;
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
        var effectId = TaskId.Parse("root/$once:reference");
        var reference = Guid.NewGuid();
        var request = CreateRequest(
            1,
            () => DurableTask.Run(_ => DurableTask.Once("reference", () => reference)));

        runtime.ScheduleFromInbox(taskId, request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => transport.Completions.Count == 1);
        await WaitUntilIdleAsync(runtime);

        var effect = storage.Get(effectId);
        Assert.Equal("$once", effect.RequestFingerprint);
        Assert.Equal(reference, effect.Result!.GetResult<Guid>());
        Assert.Null(effect.Request);
        Assert.True(effect.RemoteTarget.IsDefault);
        Assert.Equal(reference, storage.Get(taskId).Result!.GetResult<Guid>());

        // Delivering the task's completion deletes its tree, which holds the effect's record.
        ((IOutboxMessageSource)runtime).OnRetired(Assert.Single(transport.Completions).MessageId, OutboxMessageRetirement.Delivered());

        Assert.False(storage.Contains(taskId));
        Assert.False(storage.Contains(effectId));
    });
}

using Orleans.Configuration;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Messaging;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Attempts run in segments, each a local request on the activation: the code between two durable waits.
/// </summary>
public sealed partial class DurableTaskRuntimeInvariantTests
{
    [Fact]
    public Task ParkedAttemptResumesOnlyOnceTheRunningSegmentEnds() => ActivationScheduler.RunAsync(async () =>
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (runtime, _, manager, _) = CreateRuntime();
        await runtime.RecoverAsync(cancellationToken);
        var parkedId = TaskId.Parse("parked");
        var blockingId = TaskId.Parse("blocking");
        var resumed = false;
        var blockingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await ((IDurableTaskServer)runtime).ScheduleAsync(parkedId, CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            await new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(2)).WithId("call");
            resumed = true;
            return 3;
        })), cancellationToken);
        await WaitUntilAsync(() => ((IInboxWorkSource)runtime).GetWakeupDelay() is null);

        // The parked attempt's segment ended, so the activation admits another attempt, whose segment holds it.
        await ((IDurableTaskServer)runtime).ScheduleAsync(blockingId, CreateRequest(4, () => DurableTask.Run(async _ =>
        {
            blockingStarted.TrySetResult();
            await release.Task;
            return 5;
        })), cancellationToken);
        await blockingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        // The call's result commits while the other segment runs, so the parked attempt is runnable but doesn't resume.
        runtime.AcceptResponseFromInbox(parkedId.Child("call"), DurableTaskResponse.FromResult(2), RemoteTarget);
        await manager.WriteStateAsync(cancellationToken);
        for (var yields = 0; yields < 20; yields++)
        {
            await Task.Yield();
        }

        Assert.False(resumed);
        Assert.Equal(new DurableTaskOptions().RunnableExecutionWakeupDelay, ((IInboxWorkSource)runtime).GetWakeupDelay());

        release.SetResult();
        Assert.Equal(5, (await WaitForResponseAsync(runtime, blockingId)).GetResult<int>());
        Assert.Equal(3, (await WaitForResponseAsync(runtime, parkedId)).GetResult<int>());
        Assert.True(resumed);
        await runtime.StopAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    });

    [Fact]
    public Task DeactivationDiscardsAParkedAttemptWithoutResumingIt() => ActivationScheduler.RunAsync(async () =>
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (runtime, storage, _, _) = CreateRuntime();
        await runtime.RecoverAsync(cancellationToken);
        var rootId = TaskId.Parse("root");
        var parked = false;
        var resumed = false;

        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            try
            {
                parked = true;
                await new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(2)).WithId("call");
            }
            finally
            {
                resumed = true;
            }

            return 3;
        })), cancellationToken);
        await WaitUntilAsync(() => parked && ((IInboxWorkSource)runtime).GetWakeupDelay() is null);

        // The stop doesn't wait for the parked attempt, which runs no more code: the next activation replays it.
        await runtime.StopAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        for (var yields = 0; yields < 20; yields++)
        {
            await Task.Yield();
        }

        Assert.False(resumed);
        Assert.Null(storage.Get(rootId).Result);
    });

    [Fact]
    public Task WorkSourceReportsRecoveredExecutionsBeforeRecoveryStartsThem() => ActivationScheduler.RunAsync(async () =>
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (runtime, storage, manager, _) = CreateRuntime();
        await runtime.RecoverAsync(cancellationToken);
        var rootId = TaskId.Parse("root");
        runtime.ScheduleFromInbox(rootId, CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            await new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(2)).WithId("call");
            return 3;
        })));
        await manager.WriteStateAsync(cancellationToken);
        await WaitUntilAsync(() => ((IInboxWorkSource)runtime).GetWakeupDelay() is null);

        // Messaging consults its work sources as the activation starts, before the runtime recovers, so a replacement
        // activation reports the pending execution from storage and re-arms the wake-up.
        var (replacement, _, _, _) = CreateRuntime(storage, manager);
        Assert.Equal(new DurableTaskOptions().RunnableExecutionWakeupDelay, ((IInboxWorkSource)replacement).GetWakeupDelay());

        await replacement.RecoverAsync(cancellationToken);
        replacement.StartRecoveredExecutions();
        await WaitUntilAsync(() => ((IInboxWorkSource)replacement).GetWakeupDelay() is null);
        await replacement.StopAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    });
}

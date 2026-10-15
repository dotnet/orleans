using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks.Storage;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>Children which a durable execution starts shielded.</summary>
public sealed partial class DurableTaskRuntimeInvariantTests
{
    [Fact]
    public Task ShieldedChildrenStartedAfterTheCancellationRun() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var rootId = TaskId.CreateRoot("order");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.ScheduleFromInbox(rootId, CreateRequest(1, () => DurableTask.Run(async cancellationToken =>
        {
            try
            {
                await BlockedChild(started).WithId("work");
                return "completed";
            }
            catch (OperationCanceledException) when (DurableTask.Current.IsCancellationRequested)
            {
                var release = await DurableTask.Run(async _ => 1 + await DurableTask.Run(_ => 1).WithId("inner")).WithId("release").Shielded();
                await DurableTask.Delay(TimeSpan.FromMinutes(1)).WithId("pause").Shielded();
                var batch = await DurableTask.WhenAll<int>([DurableTask.Run(_ => 3), DurableTask.Run(_ => 4)]).Shielded();
                var outcome = $"{release}{batch[0]}{batch[1]}|{cancellationToken.IsCancellationRequested}";
                return $"{outcome}|{DurableTask.Current.IsCancellationRequested}";
            }
        })));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);

        await ((IDurableTaskManager)runtime).CancelAsync(rootId, "buyer", TestContext.Current.CancellationToken);
        await transport.ResumeSent.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
        var resume = Assert.Single(transport.Resumes);
        await HandleAsync(CreateHandler(runtime), ResumeContext(runtime, resume.TaskId, resume.Generation, resume.DueTime));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal("234|True|True", (await WaitForResponseAsync(runtime, rootId)).GetResult<string>());
        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(rootId.Child("work")).Result!.Status);
        foreach (var child in new[] { "release", "pause" })
        {
            var state = storage.Get(rootId.Child(child));
            Assert.True(state.Shielded);
            Assert.Null(state.CancellationRequestedAt);
            Assert.Equal(DurableTaskStatus.CompletedSuccessfully, state.Result!.Status);
        }

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(rootId.Child("release").Child("inner")).Result!.Status);
        Assert.False(storage.Get(rootId.Child("release").Child("inner")).Shielded);
    });

    [Fact]
    public Task CancellationDoesNotReachShieldedRemoteChildren() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var rootId = TaskId.CreateRoot("order");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeSendInvocation = (_, _, taskId, _) =>
        {
            if (taskId == rootId.Child("after"))
            {
                afterSent.TrySetResult();
            }
        };
        runtime.ScheduleFromInbox(rootId, CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            var before = new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(2)).WithId("before").Shielded().Post();
            await DurableTask.Run(static _ => { }).WithId("commit");
            ready.TrySetResult();
            await resume.Task;

            // A child which isn't shielded and starts after the cancellation is canceled without running.
            var outside = new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(3)).WithId("outside").Post();
            var after = new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(4)).WithId("after").Shielded().Post();

            await before;
            await after;
            return $"{await outside.GetStatusAsync(cancellationToken: CancellationToken.None)}";
        })));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await ready.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);

        await ((IDurableTaskManager)runtime).CancelAsync(rootId, "buyer", TestContext.Current.CancellationToken);
        Assert.Empty(transport.Cancellations);
        Assert.Null(storage.Get(rootId.Child("before")).CancellationRequestedAt);
        resume.SetResult();
        await afterSent.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
        var target = CreateRemoteRequest(0).Context!.TargetId;
        runtime.AcceptResponseFromInbox(rootId.Child("before"), DurableTaskResponse.Completed, target);
        runtime.AcceptResponseFromInbox(rootId.Child("after"), DurableTaskResponse.Completed, target);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(nameof(DurableTaskStatus.Canceled), (await WaitForResponseAsync(runtime, rootId)).GetResult<string>());
        Assert.Equal(
            [rootId.Child("before"), rootId.Child("after")],
            transport.Invocations.Select(static invocation => invocation.TaskId));
        Assert.Empty(transport.Cancellations);
        Assert.True(storage.Get(rootId.Child("after")).Shielded);
    });

    [Fact]
    public Task RecoveryKeepsShieldedChildrenUnreachedByTheCancellation() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var childId = rootId.Child("cleanup");
        var childAttempts = 0;
        var firstChildAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            var parent = DurableTask.Current;
            return await DurableTask.Run(async cancellationToken =>
            {
                var child = DurableTask.Current;
                if (Interlocked.Increment(ref childAttempts) == 1)
                {
                    firstChildAttempt.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return $"{child.IsCancellationRequested}|{cancellationToken.IsCancellationRequested}|{parent.IsCancellationRequested}";
            }).WithId("cleanup").Shielded();
        }));
        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await firstChildAttempt.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);

        await ((IDurableTaskManager)runtime).CancelAsync(rootId, "buyer", TestContext.Current.CancellationToken);
        Assert.Null(storage.Get(childId).CancellationRequestedAt);
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);

        var (replacement, _, _, _) = CreateRuntime(storage, manager);
        await replacement.ResumePendingTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal("False|False|True", (await WaitForResponseAsync(replacement, rootId)).GetResult<string>());
        Assert.Equal(2, Volatile.Read(ref childAttempts));
        Assert.Equal("buyer", storage.Get(rootId).CancellationReason);
        Assert.True(storage.Get(childId).Shielded);
        Assert.Null(storage.Get(childId).CancellationRequestedAt);
    });

    [Fact]
    public Task AbortReachesShieldedChildren() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var childId = rootId.Child("cleanup");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childEnded = new TaskCompletionSource<(bool TokenCanceled, bool CancellationRequested)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            return await DurableTask.Run(async cancellationToken =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    childEnded.TrySetResult((cancellationToken.IsCancellationRequested, DurableTask.Current.IsCancellationRequested));
                }

                return 1;
            }).WithId("cleanup").Shielded();
        }));
        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);

        await ((IDurableTaskManager)runtime).AbortAsync(rootId, "operator", TestContext.Current.CancellationToken);

        AssertAborted(await WaitForResponseAsync(runtime, rootId), "operator");
        AssertAborted(storage.Get(childId).Result, "operator");
        Assert.Equal((true, false), await childEnded.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken));
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
    });

    [Fact]
    public Task ShieldedChildIsCanceledThroughItsOwnHandle() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var childId = rootId.Child("cleanup");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            var child = await BlockedChild(started).WithId("cleanup").Shielded().ScheduleAsync(CancellationToken.None);

            await child.CancelAsync("no longer needed", CancellationToken.None);
            return (await child.GetResponseAsync(CancellationToken.None)).Status;
        }));

        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);

        Assert.Equal(DurableTaskStatus.Canceled, (await WaitForResponseAsync(runtime, rootId)).GetResult<DurableTaskStatus>());
        Assert.Equal("no longer needed", storage.Get(childId).CancellationReason);
        Assert.True(storage.Get(childId).Shielded);
    });

    [Fact]
    public async Task VolatileStorageRecoversTheFirstCancellationReasonAndTheShield()
    {
        using var services = new ServiceCollection()
            .AddSerializer(builder => builder.AddAssembly(typeof(DurableTaskMessageTransport).Assembly))
            .BuildServiceProvider();
        var storage = new VolatileDurableTaskGrainStorage(
            services.GetRequiredService<DeepCopier<Dictionary<TaskId, DurableTaskState>>>(),
            services.GetRequiredService<DeepCopier<DurableTaskState>>(),
            TimeProvider.System,
            services.GetRequiredService<Serializer>());
        var taskId = TaskId.Parse("root/cleanup");
        var state = storage.GetOrCreateTask(taskId, request: null);

        storage.Shield(taskId, state);
        storage.RequestCancellation(taskId, state, "buyer");
        Assert.True(storage.TryGetTask(taskId, out var canceled));
        storage.RequestCancellation(taskId, canceled, "later");
        await storage.WriteAsync(TestContext.Current.CancellationToken);
        storage.Clear();
        await storage.ReadAsync(TestContext.Current.CancellationToken);

        Assert.True(storage.TryGetTask(taskId, out var recovered));
        Assert.Equal(("buyer", true), (recovered.CancellationReason, recovered.Shielded));
        Assert.NotNull(recovered.CancellationRequestedAt);
    }

    [Fact]
    public void JsonTaskStateStoresTheShieldOnlyWhenSet()
    {
        var typeInfo = DurableTaskJsonContext.Default.DurableTaskState;

        var json = JsonSerializer.Serialize(new DurableTaskState { Shielded = true }, typeInfo);

        Assert.True(JsonSerializer.Deserialize(json, typeInfo)!.Shielded);
        Assert.DoesNotContain(nameof(DurableTaskState.Shielded), JsonSerializer.Serialize(new DurableTaskState(), typeInfo), StringComparison.Ordinal);
    }
}

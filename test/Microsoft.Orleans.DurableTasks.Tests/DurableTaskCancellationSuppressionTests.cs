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

/// <summary>Children which a durable execution schedules in a cancellation suppression scope.</summary>
public sealed partial class DurableTaskRuntimeInvariantTests
{
    [Fact]
    public Task SuppressedChildrenScheduledAfterTheCancellationRun() => ActivationScheduler.RunAsync(async () =>
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
            catch (OperationCanceledException) when (DurableExecutionContext.Current!.IsCancellationRequested)
            {
                using (DurableExecutionContext.SuppressCancellation())
                {
                    var release = await DurableTask.Run(async _ => 1 + await DurableTask.Run(_ => 1).WithId("inner")).WithId("release");
                    await DurableTask.Delay(TimeSpan.FromMinutes(1)).WithId("pause");
                    var batch = await DurableTask.WhenAll<int>([DurableTask.Run(_ => 3), DurableTask.Run(_ => 4)]);
                    var outcome = $"{release}{batch[0]}{batch[1]}|{cancellationToken.IsCancellationRequested}";
                    return $"{outcome}|{DurableExecutionContext.Current.IsCancellationRequested}";
                }
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
            Assert.True(state.CancellationSuppressed);
            Assert.Null(state.CancellationRequestedAt);
            Assert.Equal(DurableTaskStatus.CompletedSuccessfully, state.Result!.Status);
        }

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(rootId.Child("release").Child("inner")).Result!.Status);
        Assert.False(storage.Get(rootId.Child("release").Child("inner")).CancellationSuppressed);
    });

    [Fact]
    public Task CancellationDoesNotReachSuppressedRemoteChildren() => ActivationScheduler.RunAsync(async () =>
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
            ScheduledTask before;
            using (DurableExecutionContext.SuppressCancellation())
            {
                before = await new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(2)).WithId("before").ScheduleAsync(CancellationToken.None);
            }

            ready.TrySetResult();
            await resume.Task;

            // Outside the scope, a child scheduled after the cancellation is canceled without running.
            var outside = await new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(3)).WithId("outside").ScheduleAsync(CancellationToken.None);
            ScheduledTask after;
            using (DurableExecutionContext.SuppressCancellation())
            {
                after = await new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(4)).WithId("after").ScheduleAsync(CancellationToken.None);
            }

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
        Assert.True(storage.Get(rootId.Child("after")).CancellationSuppressed);
    });

    [Fact]
    public Task RecoveryKeepsSuppressedChildrenUnreachedByTheCancellation() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var childId = rootId.Child("cleanup");
        var childAttempts = 0;
        var firstChildAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            var parent = DurableExecutionContext.Current!;
            using (DurableExecutionContext.SuppressCancellation())
            {
                return await DurableTask.Run(async cancellationToken =>
                {
                    var child = DurableExecutionContext.Current!;
                    if (Interlocked.Increment(ref childAttempts) == 1)
                    {
                        firstChildAttempt.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }

                    return $"{child.IsCancellationRequested}|{cancellationToken.IsCancellationRequested}|{parent.IsCancellationRequested}";
                }).WithId("cleanup");
            }
        }));
        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await firstChildAttempt.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);

        await ((IDurableTaskManager)runtime).CancelAsync(rootId, "buyer", TestContext.Current.CancellationToken);
        Assert.Null(storage.Get(childId).CancellationRequestedAt);
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);

        var (replacement, _, _, _) = CreateRuntime(storage, manager);
        await replacement.OnActivateAsync(TestContext.Current.CancellationToken);

        Assert.Equal("False|False|True", (await WaitForResponseAsync(replacement, rootId)).GetResult<string>());
        Assert.Equal(2, Volatile.Read(ref childAttempts));
        Assert.Equal("buyer", storage.Get(rootId).CancellationReason);
        Assert.True(storage.Get(childId).CancellationSuppressed);
        Assert.Null(storage.Get(childId).CancellationRequestedAt);
    });

    [Fact]
    public Task AbortReachesSuppressedChildren() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var childId = rootId.Child("cleanup");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childEnded = new TaskCompletionSource<(bool TokenCanceled, bool CancellationRequested)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            using (DurableExecutionContext.SuppressCancellation())
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
                        childEnded.TrySetResult((cancellationToken.IsCancellationRequested, DurableExecutionContext.Current!.IsCancellationRequested));
                    }

                    return 1;
                }).WithId("cleanup");
            }
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
    public Task SuppressedChildIsCanceledThroughItsOwnHandle() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var childId = rootId.Child("cleanup");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            ScheduledTask<int> child;
            using (DurableExecutionContext.SuppressCancellation())
            {
                child = await BlockedChild(started).WithId("cleanup").ScheduleAsync(CancellationToken.None);
            }

            await child.CancelAsync("no longer needed", CancellationToken.None);
            return (await child.GetResponseAsync(CancellationToken.None)).Status;
        }));

        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);

        Assert.Equal(DurableTaskStatus.Canceled, (await WaitForResponseAsync(runtime, rootId)).GetResult<DurableTaskStatus>());
        Assert.Equal("no longer needed", storage.Get(childId).CancellationReason);
        Assert.True(storage.Get(childId).CancellationSuppressed);
    });

    [Fact]
    public async Task VolatileStorageRecoversTheFirstCancellationReasonAndTheSuppression()
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

        storage.SuppressCancellation(taskId, state);
        storage.RequestCancellation(taskId, state, "buyer");
        Assert.True(storage.TryGetTask(taskId, out var canceled));
        storage.RequestCancellation(taskId, canceled, "later");
        await storage.WriteAsync(TestContext.Current.CancellationToken);
        storage.Clear();
        await storage.ReadAsync(TestContext.Current.CancellationToken);

        Assert.True(storage.TryGetTask(taskId, out var recovered));
        Assert.Equal(("buyer", true), (recovered.CancellationReason, recovered.CancellationSuppressed));
        Assert.NotNull(recovered.CancellationRequestedAt);
    }

    [Fact]
    public void JsonTaskStateStoresCancellationSuppressionOnlyWhenSet()
    {
        var typeInfo = DurableTaskJsonContext.Default.DurableTaskState;

        var json = JsonSerializer.Serialize(new DurableTaskState { CancellationSuppressed = true }, typeInfo);

        Assert.True(JsonSerializer.Deserialize(json, typeInfo)!.CancellationSuppressed);
        Assert.DoesNotContain(nameof(DurableTaskState.CancellationSuppressed), JsonSerializer.Serialize(new DurableTaskState(), typeInfo), StringComparison.Ordinal);
    }
}

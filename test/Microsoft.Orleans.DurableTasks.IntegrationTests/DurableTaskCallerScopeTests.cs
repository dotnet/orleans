using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Journaling;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>
/// Verifies that the remote children of two callers whose workflows share a root ID and a child name stay separate on
/// their shared callee.
/// </summary>
public abstract class DurableTaskCallerScopeTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly CallerScopeProbe _probe = new();
    private readonly DurableTaskRecoveryFixture _fixture;

    protected DurableTaskCallerScopeTests(string journalFormatKey)
    {
        _fixture = new(journalFormatKey, silo => silo.Services.AddSingleton(_probe));
    }

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task CallersWithTheSameRootIdKeepSeparateChildrenOnASharedCallee()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var callee = _fixture.Client.GetGrain<ISharedCalleeGrain>(Guid.NewGuid());
        var first = _fixture.Client.GetGrain<ISharedRootCallerGrain>(Guid.NewGuid());
        var second = _fixture.Client.GetGrain<ISharedRootCallerGrain>(Guid.NewGuid());
        var calleeId = callee.GetGrainId();
        var childId = TaskId.CreateRoot("place-order").Child(SharedRootCallerGrain.CallId);
        var firstChildId = CallerScope.GetTaskId(first.GetGrainId(), childId);
        var secondChildId = CallerScope.GetTaskId(second.GetGrainId(), childId);
        using var events = DurableTaskEventBarriers.Collect();

        // Each caller's workflow is the root task 'place-order' on its own grain, and calls the callee as 'reserve'.
        var firstOrder = await first.PlaceOrderAsync(callee.GetPrimaryKey(), 1).WithId("place-order").ScheduleAsync(cancellationToken);
        var secondOrder = await second.PlaceOrderAsync(callee.GetPrimaryKey(), 2).WithId("place-order").ScheduleAsync(cancellationToken);
        await Task.WhenAll(_probe.Started(1), _probe.Started(2)).WaitAsync(Timeout, cancellationToken);

        var running = new List<TaskId>();
        await foreach (var taskId in callee.AsReference<IDurableTaskGrainExtension>().GetRunningTasksAsync(cancellationToken))
        {
            running.Add(taskId);
        }

        Assert.Equal(2, running.Count);
        Assert.Contains(firstChildId, running);
        Assert.Contains(secondChildId, running);

        // Canceling the first workflow cancels only its own child on the callee.
        await firstOrder.CancelAsync(cancellationToken);
        Assert.Equal(DurableTaskStatus.Canceled, (await firstOrder.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken)).Status);
        Assert.Equal(DurableTaskStatus.Canceled, (await events.WaitForCompletedAsync(calleeId, firstChildId)).Status);
        Assert.True(_probe.Canceled(1).IsCompleted);
        Assert.False(_probe.Canceled(2).IsCompleted);

        // The second workflow's child completes with its own result.
        _probe.Release(2);
        Assert.Equal(2, (await secondOrder.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken)).GetResult<int>());
        Assert.Equal(2, (await events.WaitForCompletedAsync(calleeId, secondChildId)).GetResult<int>());

        // The callee deletes each child once its own caller accepted its completion.
        await events.WaitForDeletedAsync(calleeId, firstChildId);
        await events.WaitForDeletedAsync(calleeId, secondChildId);
    }
}

/// <summary>Runs caller scope coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskCallerScopeTests() : DurableTaskCallerScopeTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs caller scope coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskCallerScopeTests() : DurableTaskCallerScopeTests(DurableTaskRecoveryFixture.OrleansBinary);

/// <summary>Holds the callee's tasks, keyed by their values, until the test releases them or they're canceled.</summary>
public sealed class CallerScopeProbe
{
    private readonly ConcurrentDictionary<(string Kind, int Value), TaskCompletionSource> _signals = new();

    public Task Started(int value) => Get("started", value).Task;
    public Task Canceled(int value) => Get("canceled", value).Task;
    public void Release(int value) => Get("released", value).TrySetResult();

    public async Task HoldAsync(int value, CancellationToken cancellationToken)
    {
        Get("started", value).TrySetResult();
        try
        {
            await Get("released", value).Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Get("canceled", value).TrySetResult();
            throw;
        }
    }

    private TaskCompletionSource Get(string kind, int value) =>
        _signals.GetOrAdd((kind, value), static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
}

public interface ISharedCalleeGrain : IGrainWithGuidKey
{
    DurableTask<int> HoldAsync(int value);
}

public interface ISharedRootCallerGrain : IGrainWithGuidKey
{
    DurableTask<int> PlaceOrderAsync(Guid calleeKey, int value);
}

[GrainType("durable-task-shared-callee-test")]
public sealed class SharedCalleeGrain(CallerScopeProbe probe) : DurableGrain, ISharedCalleeGrain
{
    public DurableTask<int> HoldAsync(int value) =>
        DurableTask.Run(async cancellationToken =>
        {
            await probe.HoldAsync(value, cancellationToken);
            return value;
        });
}

[GrainType("durable-task-shared-root-caller-test")]
public sealed class SharedRootCallerGrain : DurableGrain, ISharedRootCallerGrain
{
    public const string CallId = "reserve";

    public async DurableTask<int> PlaceOrderAsync(Guid calleeKey, int value) =>
        await GrainFactory.GetGrain<ISharedCalleeGrain>(calleeKey).HoldAsync(value).WithId(CallId);
}

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Journaling;
using Orleans.Messaging;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>
/// Verifies that deleting a completed task's execution tree cancels the work which the task started and never observed:
/// a running local child, and a pending call to another grain.
/// </summary>
public abstract class DurableTaskTreeDeletionTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly TreeDeletionProbe _probe = new();
    private readonly DurableTaskRecoveryFixture _fixture;

    protected DurableTaskTreeDeletionTests(string journalFormatKey)
    {
        _fixture = new(journalFormatKey, silo => silo.Services.AddSingleton(_probe));
    }

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task DeletingACompletedTasksTreeCancelsTheWorkWhichItLeftRunning()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var starter = _fixture.Client.GetGrain<ITreeDeletionStarterGrain>(Guid.NewGuid());
        var owner = _fixture.Client.GetGrain<ITreeDeletionOwnerGrain>(Guid.NewGuid());
        var worker = _fixture.Client.GetGrain<ITreeDeletionWorkerGrain>(Guid.NewGuid());
        var ownerId = owner.GetGrainId();
        var workerId = worker.GetGrainId();
        var startId = TaskId.CreateRoot("start");
        using var events = DurableTaskEventBarriers.Collect();

        // The owner's task starts a local child and a call to the worker, and completes without observing either.
        var start = await starter.StartAsync(owner.GetPrimaryKey(), worker.GetPrimaryKey()).WithId("start").ScheduleAsync(cancellationToken);
        Assert.Equal(TreeDeletionOwnerGrain.Returned, (await start.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken)).GetResult<string>());
        var ownedId = await events.WaitForCallerChildAsync(ownerId, starter.GetGrainId(), startId.Child(TreeDeletionStarterGrain.CallId));
        var localId = ownedId.Child(TreeDeletionOwnerGrain.LocalId);
        var remoteId = ownedId.Child(TreeDeletionOwnerGrain.RemoteId);
        var workerTaskId = await events.WaitForCallerChildAsync(workerId, ownerId, remoteId);

        // The starter accepts the owner's completion, which deletes the owner's tree with the work which is still running.
        await events.WaitForDeletedAsync(ownerId, ownedId);
        await events.WaitForDeletedAsync(ownerId, localId);
        await events.WaitForDeletedAsync(ownerId, remoteId);

        // The local child's attempt is canceled without task cancellation, and its cleanup commits.
        var local = await _probe.LocalUnwound.WaitAsync(Timeout, cancellationToken);
        Assert.False(local.TaskCancellationRequested);
        Assert.True(local.AttemptCanceled);
        Assert.Null(local.CleanupFailure);

        // The worker's task receives the deletion as durable cancellation through the owner's outbox.
        Assert.True(await _probe.RemoteCanceled.WaitAsync(Timeout, cancellationToken));
        Assert.Equal(DurableTaskStatus.Canceled, (await events.WaitForCompletedAsync(workerId, workerTaskId)).Status);

        // The owner accepts the canceled task's completion without recreating it, so the worker deletes its task.
        await events.WaitForDeletedAsync(workerId, workerTaskId);
        Assert.Empty(await owner.GetInboxDeadLetterCodesAsync());
        Assert.Empty(await GetTasksAsync(owner));
        Assert.Equal(TreeDeletionOwnerGrain.CleanedUp, await owner.GetCleanupAsync());

        // The committed journal holds the canceled attempt's cleanup.
        Assert.True(_fixture.Cluster.TryGetGrainContext(ownerId, out var activation));
        await owner.RequestDeactivationAsync();
        await activation.Deactivated.WaitAsync(Timeout, cancellationToken);
        Assert.Equal(TreeDeletionOwnerGrain.CleanedUp, await owner.GetCleanupAsync());
        Assert.Empty(await GetTasksAsync(owner));
    }

    private static async Task<List<TaskId>> GetTasksAsync(IAddressable grain)
    {
        var tasks = new List<TaskId>();
        await foreach (var (taskId, _) in grain.AsReference<IDurableTaskGrainExtension>().GetTasksAsync(TestContext.Current.CancellationToken))
        {
            tasks.Add(taskId);
        }

        return tasks;
    }
}

/// <summary>Runs tree deletion coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskTreeDeletionTests() : DurableTaskTreeDeletionTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs tree deletion coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskTreeDeletionTests() : DurableTaskTreeDeletionTests(DurableTaskRecoveryFixture.OrleansBinary);

/// <summary>What the local child's attempt observed as it unwound.</summary>
public sealed record LocalUnwinding(bool TaskCancellationRequested, bool AttemptCanceled, Exception? CleanupFailure);

/// <summary>Records what the owner's unobserved work observes when the owner's tree is deleted.</summary>
public sealed class TreeDeletionProbe
{
    private readonly TaskCompletionSource<LocalUnwinding> _localUnwound = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _remoteCanceled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the local child's attempt unwinds.</summary>
    public Task<LocalUnwinding> LocalUnwound => _localUnwound.Task;

    /// <summary>Completes once the worker's task observes cancellation, with whether it was task cancellation.</summary>
    public Task<bool> RemoteCanceled => _remoteCanceled.Task;

    public void RecordLocalUnwound(LocalUnwinding unwinding) => _localUnwound.TrySetResult(unwinding);

    public void RecordRemoteCanceled(bool taskCancellationRequested) => _remoteCanceled.TrySetResult(taskCancellationRequested);
}

public interface ITreeDeletionStarterGrain : IGrainWithGuidKey
{
    DurableTask<string> StartAsync(Guid ownerKey, Guid workerKey);
}

public interface ITreeDeletionOwnerGrain : IGrainWithGuidKey
{
    /// <summary>Starts a local child and a call to the worker, and returns without observing either.</summary>
    DurableTask<string> StartWorkAndReturnAsync(Guid workerKey);

    Task<string?> GetCleanupAsync();

    Task<string?[]> GetInboxDeadLetterCodesAsync();

    Task RequestDeactivationAsync();
}

public interface ITreeDeletionWorkerGrain : IGrainWithGuidKey
{
    /// <summary>Waits until its task is canceled.</summary>
    DurableTask<string> HoldAsync();
}

[GrainType("durable-task-tree-deletion-starter-test")]
public sealed class TreeDeletionStarterGrain : DurableGrain, ITreeDeletionStarterGrain
{
    public const string CallId = "own";

    public async DurableTask<string> StartAsync(Guid ownerKey, Guid workerKey) =>
        await GrainFactory.GetGrain<ITreeDeletionOwnerGrain>(ownerKey).StartWorkAndReturnAsync(workerKey).WithId(CallId);
}

[GrainType("durable-task-tree-deletion-owner-test")]
public sealed class TreeDeletionOwnerGrain(
    TreeDeletionProbe probe,
    IMessagingDiagnostics diagnostics,
    [FromKeyedServices("cleanup")] IDurableValue<string> cleanup)
    : DurableGrain, ITreeDeletionOwnerGrain
{
    public const string LocalId = "local";
    public const string RemoteId = "remote";
    public const string Returned = "returned";
    public const string CleanedUp = "cleaned up";

    public async DurableTask<string> StartWorkAndReturnAsync(Guid workerKey)
    {
        // The task completes while its children run, so deleting its tree cancels them.
#pragma warning disable ORLEANS0219
        _ = await DurableTask.Run(RunLocalWorkAsync).WithId(LocalId).ScheduleAsync();
        _ = await GrainFactory.GetGrain<ITreeDeletionWorkerGrain>(workerKey).HoldAsync().WithId(RemoteId).ScheduleAsync();
#pragma warning restore ORLEANS0219
        return Returned;
    }

    public Task<string?> GetCleanupAsync() => Task.FromResult(cleanup.Value);

    public Task<string?[]> GetInboxDeadLetterCodesAsync() =>
        Task.FromResult(diagnostics.InboxDeadLetters.Select(deadLetter => deadLetter.Code).ToArray());

    public Task RequestDeactivationAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    private async Task RunLocalWorkAsync(CancellationToken cancellationToken)
    {
        var context = DurableTask.Current;
        try
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var failure = await global::Xunit.Record.ExceptionAsync(async () =>
            {
                cleanup.Value = CleanedUp;
                await WriteStateAsync();
            });
            probe.RecordLocalUnwound(new(context.IsTaskCancellationRequested, context.AttemptCancellationToken.IsCancellationRequested, failure));
            throw;
        }
    }
}

[GrainType("durable-task-tree-deletion-worker-test")]
public sealed class TreeDeletionWorkerGrain(TreeDeletionProbe probe) : DurableGrain, ITreeDeletionWorkerGrain
{
    public DurableTask<string> HoldAsync() =>
        DurableTask.Run<string>(async cancellationToken =>
        {
            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                probe.RecordRemoteCanceled(DurableTask.Current.IsTaskCancellationRequested);
                throw;
            }

            return "released";
        });
}

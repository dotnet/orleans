using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.DurableTasks;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>
/// Verifies that a root task ID runs again once its grain deleted its earlier run, within Messaging's deduplication
/// window.
/// </summary>
/// <remarks>
/// The result retention period is shorter than the deduplication window. Each run of the root has its own incarnation,
/// which its call's wire ID covers, so Messaging delivers the later run's invocation rather than deduplicating it against
/// the earlier run's, and the callee stores the later call under another ID.
/// </remarks>
public abstract class DurableTaskRerunTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(1);
    private readonly DurableTaskRecoveryFixture _fixture;

    protected DurableTaskRerunTests(string journalFormatKey)
    {
        _fixture = new(
            journalFormatKey,
            silo => silo.Services.Configure<DurableTaskOptions>(options => options.ResultRetentionPeriod = Retention));
    }

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task RootWithARemoteChildRunsAgainAfterItsEarlierRunWasDeleted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var caller = _fixture.Client.GetGrain<IRerunCallerGrain>(Guid.NewGuid());
        var callee = _fixture.Client.GetGrain<IRerunCalleeGrain>(Guid.NewGuid());
        var callerId = caller.GetGrainId();
        var calleeId = callee.GetGrainId();
        var rootId = TaskId.CreateRoot("rerun");
        var callId = rootId.Child(RerunCallerGrain.CallId);
        using var events = DurableTaskEventBarriers.Collect();

        // The first run calls the callee, which deletes its task once the caller accepted the completion.
        var first = await caller.RunAsync(callee.GetPrimaryKey(), 7).WithId("rerun").ScheduleAsync(cancellationToken);
        Assert.Equal(7, (await first.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken)).GetResult<int>());
        var firstChildId = await events.WaitForCallerChildAsync(calleeId, callerId, callId);
        await events.WaitForDeletedAsync(calleeId, firstChildId);

        // The first run's retention ends, well within the deduplication window, so the next schedule of the same root ID
        // deletes it and runs the root again with the same request.
        _fixture.JobClock.Advance(Retention + TimeSpan.FromMinutes(1));
        var second = await caller.RunAsync(callee.GetPrimaryKey(), 7).WithId("rerun").ScheduleAsync(cancellationToken);
        await events.WaitForDeletedAsync(callerId, rootId);

        // The second run's call reaches the callee as a new task, and the run completes.
        Assert.Equal(7, (await second.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken)).GetResult<int>());
        var secondCreated = await events.WaitForEventAsync(
            nameof(global::Orleans.DurableTasks.Diagnostics.DurableTaskEvents.TaskCreated),
            evt => evt.Payload is global::Orleans.DurableTasks.Diagnostics.DurableTaskEvents.TaskCreated payload
                && payload.GrainContext.GrainId == calleeId
                && payload.TaskId != firstChildId
                && DurableTaskEventBarriers.IsCallerChild(callerId, callId, payload.TaskId),
            Timeout,
            cancellationToken);
        Assert.NotNull(secondCreated.Payload);
    }
}

/// <summary>Runs root re-run coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskRerunTests() : DurableTaskRerunTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs root re-run coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskRerunTests() : DurableTaskRerunTests(DurableTaskRecoveryFixture.OrleansBinary);

public interface IRerunCallerGrain : IGrainWithGuidKey
{
    DurableTask<int> RunAsync(Guid calleeKey, int value);
}

public interface IRerunCalleeGrain : IGrainWithGuidKey
{
    DurableTask<int> EchoAsync(int value);
}

[GrainType("durable-task-rerun-caller-test")]
public sealed class RerunCallerGrain : DurableGrain, IRerunCallerGrain
{
    public const string CallId = "echo";

    public async DurableTask<int> RunAsync(Guid calleeKey, int value) =>
        await GrainFactory.GetGrain<IRerunCalleeGrain>(calleeKey).EchoAsync(value).WithId(CallId);
}

[GrainType("durable-task-rerun-callee-test")]
public sealed class RerunCalleeGrain : DurableGrain, IRerunCalleeGrain
{
    public DurableTask<int> EchoAsync(int value) => DurableTask.FromResult(value);
}

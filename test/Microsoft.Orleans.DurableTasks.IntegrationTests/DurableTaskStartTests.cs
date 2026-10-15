using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Journaling;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>
/// Exercises how clients, ordinary grain methods, and durable executions start tasks, and in-grain task management through
/// <see cref="IDurableTaskManager"/>.
/// </summary>
public abstract class DurableTaskStartTests(string journalFormatKey) : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly DurableTaskRecoveryFixture _fixture = new(journalFormatKey);

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task ClientStartsRootsUnderTheirNamesOrUnderNewRandomIds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var worker = _fixture.Client.GetGrain<IDurableTaskWorkerGrain>(Guid.NewGuid());

        var named = await worker.EchoAsync(4).WithId("client-scheduled").ScheduleAsync(cancellationToken);
        var unnamed = await worker.EchoAsync(5).ScheduleAsync(cancellationToken);

        Assert.Equal(TaskId.CreateRoot("client-scheduled"), named.Id);
        Assert.True(Guid.TryParseExact(unnamed.Id.ToString(), "N", out _));
        Assert.Equal(8, await named.WaitAsync(Timeout, cancellationToken));
        Assert.Equal(10, await unnamed.WaitAsync(Timeout, cancellationToken));
        Assert.Equal(8, (await worker.GetDurableTask<int>("client-scheduled").GetResponseAsync(cancellationToken)).GetResult<int>());
        Assert.Equal(14, await worker.EchoAsync(7));
    }

    [Fact]
    public void ClientCannotPost()
    {
        var worker = _fixture.Client.GetGrain<IDurableTaskWorkerGrain>(Guid.NewGuid());

        var exception = Assert.Throws<InvalidOperationException>(() => worker.EchoAsync(1).WithId("posted").Post());

        Assert.Contains("durable outbox", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryGrainMethodPostsRootWithItsWriteAndTheRootIsPolledById()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var starter = _fixture.Client.GetGrain<IDurableTaskStartingGrain>(Guid.NewGuid());
        var workerKey = Guid.NewGuid();
        var started = _fixture.MessagingProbe.WaitForStartedAsync(workerKey, 1);

        await starter.PostEchoAsync(workerKey, 21, "posted-echo");
        await started.WaitAsync(Timeout, cancellationToken);

        var posted = _fixture.Client.GetGrain<IDurableTaskWorkerGrain>(workerKey).GetDurableTask<int>("posted-echo");
        var response = await posted.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);
        Assert.Equal(42, response.GetResult<int>());

        // The starter retains no task for the posted call.
        Assert.Empty(await GetTaskIdsAsync(starter, cancellationToken));
    }

    [Fact]
    public async Task OrdinaryGrainMethodSchedulesOnItsOwnGrainAndOnAnother()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var starter = _fixture.Client.GetGrain<IDurableTaskStartingGrain>(Guid.NewGuid());

        Assert.Equal((6, 22), await starter.ScheduleAndAwaitAsync(Guid.NewGuid(), 11).WaitAsync(Timeout, cancellationToken));
    }

    [Fact]
    public async Task WorkflowPostsDetachedRootAndRetainsOnlyItsRecord()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var starter = _fixture.Client.GetGrain<IDurableTaskStartingGrain>(Guid.NewGuid());
        var workerKey = Guid.NewGuid();
        var started = _fixture.MessagingProbe.WaitForStartedAsync(workerKey, 1);

        var workflow = await starter.PostEchoInWorkflowAsync(workerKey, 5, "workflow-echo").WithId("workflow").ScheduleAsync(cancellationToken);
        await workflow.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);
        await started.WaitAsync(Timeout, cancellationToken);

        var detached = _fixture.Client.GetGrain<IDurableTaskWorkerGrain>(workerKey).GetDurableTask<int>("workflow-echo");
        var response = await detached.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);
        Assert.Equal(10, response.GetResult<int>());

        // The starter retains the client-scheduled workflow with the record in its tree, but no task for the detached call.
        var starterTasks = await GetTaskIdsAsync(starter, cancellationToken);
        Assert.Equal(2, starterTasks.Count);
        Assert.Contains(TaskId.CreateRoot("workflow"), starterTasks);
        var workerId = _fixture.Client.GetGrain<IDurableTaskWorkerGrain>(workerKey).GetGrainId();
        Assert.Contains(TaskId.CreateRoot("workflow").Child($"$detach:{workerId}:workflow-echo"), starterTasks);
    }

    [Fact]
    public async Task WorkflowObservesDetachedTasksThroughTheirStarts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var starter = _fixture.Client.GetGrain<IDurableTaskStartingGrain>(Guid.NewGuid());
        var workerKey = Guid.NewGuid();

        var workflow = await starter.AwaitDetachedEchoesAsync(workerKey, 3).WithId("workflow").ScheduleAsync(cancellationToken);

        // The awaited detached task derives its root ID from the workflow, and the scheduled one uses its name.
        Assert.Equal(6 + 8, await workflow.WaitAsync(Timeout, cancellationToken));
        var worker = _fixture.Client.GetGrain<IDurableTaskWorkerGrain>(workerKey);
        Assert.Equal(6, (await worker.GetDurableTask<int>("workflow:$child-1").GetResponseAsync(cancellationToken)).GetResult<int>());
        Assert.Equal(8, (await worker.GetDurableTask<int>("scheduled-echo").GetResponseAsync(cancellationToken)).GetResult<int>());

        // Outside a durable execution, awaiting a reference waits for the task's result.
        Assert.Equal(8, await worker.GetDurableTask<int>("scheduled-echo"));
    }

    [Fact]
    public async Task WorkflowScheduleFailsWhenItsHostRejectsTheStart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var starter = _fixture.Client.GetGrain<IDurableTaskStartingGrain>(Guid.NewGuid());
        var workerKey = Guid.NewGuid();
        var worker = _fixture.Client.GetGrain<IDurableTaskWorkerGrain>(workerKey);
        await worker.EchoAsync(1).WithId("taken").ScheduleAsync(cancellationToken);

        var workflow = await starter.ScheduleConflictingEchoAsync(workerKey, "taken").WithId("workflow").ScheduleAsync(cancellationToken);

        Assert.Contains("different request", await workflow.WaitAsync(Timeout, cancellationToken), StringComparison.Ordinal);

        // The worker rejected the conflicting invocation permanently, and its dead letter records the conflict's code.
        Assert.Equal([DurableTaskRejectionCodes.Conflict], await worker.GetInboxDeadLetterCodesAsync());
    }

    [Fact]
    public async Task GrainCancelsItsOwnTaskThroughTheTaskManager()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var starterKey = Guid.NewGuid();
        var starter = _fixture.Client.GetGrain<IDurableTaskStartingGrain>(starterKey);
        var started = _fixture.MessagingProbe.WaitForStartedAsync(starterKey, 1);
        var waiting = await starter.WaitForCancellationAsync().WithId("wait").ScheduleAsync(cancellationToken);
        await started.WaitAsync(Timeout, cancellationToken);

        var status = await starter.CancelThroughManagerAsync("wait").WaitAsync(Timeout, cancellationToken);

        Assert.Equal(DurableTaskStatus.Canceled, status);
        var response = await waiting.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);
        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
    }

    private static async Task<List<TaskId>> GetTaskIdsAsync(IAddressable grain, CancellationToken cancellationToken)
    {
        var taskIds = new List<TaskId>();
        await foreach (var (taskId, _) in grain.AsReference<IDurableTaskGrainExtension>().GetTasksAsync(cancellationToken))
        {
            taskIds.Add(taskId);
        }

        return taskIds;
    }
}

/// <summary>Runs task start coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskStartTests() : DurableTaskStartTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs task start coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskStartTests() : DurableTaskStartTests(DurableTaskRecoveryFixture.OrleansBinary);

public interface IDurableTaskStartingGrain : IGrainWithGuidKey
{
    Task PostEchoAsync(Guid workerKey, int value, string rootId);

    Task<(int Own, int Other)> ScheduleAndAwaitAsync(Guid workerKey, int value);

    DurableTask<int> TripleAsync(int value);

    DurableTask PostEchoInWorkflowAsync(Guid workerKey, int value, string rootId);

    DurableTask<int> AwaitDetachedEchoesAsync(Guid workerKey, int value);

    DurableTask<string> ScheduleConflictingEchoAsync(Guid workerKey, string rootId);

    DurableTask<int> WaitForCancellationAsync();

    Task<DurableTaskStatus> CancelThroughManagerAsync(string rootId);
}

[GrainType("durable-task-starting-test")]
public sealed class DurableTaskStartingGrain(IDurableTaskManager tasks, DurableTaskMessagingProbe probe)
    : DurableGrain, IDurableTaskStartingGrain
{
    public async Task PostEchoAsync(Guid workerKey, int value, string rootId)
    {
        _ = GrainFactory.GetGrain<IDurableTaskWorkerGrain>(workerKey).EchoAsync(value).WithId(rootId).Post();
        await WriteStateAsync();
    }

    public async Task<(int Own, int Other)> ScheduleAndAwaitAsync(Guid workerKey, int value)
    {
        // The grain schedules its own task through its runtime, so the call doesn't wait for the grain itself.
        var own = await this.AsReference<IDurableTaskStartingGrain>().TripleAsync(2).ScheduleAsync();
        var other = await GrainFactory.GetGrain<IDurableTaskWorkerGrain>(workerKey).EchoAsync(value).ScheduleAsync();
        return (await own, await other);
    }

    public DurableTask<int> TripleAsync(int value) => DurableTask.FromResult(value * 3);

    public DurableTask PostEchoInWorkflowAsync(Guid workerKey, int value, string rootId) =>
        DurableTask.Run(cancellationToken =>
        {
            _ = GrainFactory.GetGrain<IDurableTaskWorkerGrain>(workerKey).EchoAsync(value).Detached().WithId(rootId).Post();
        });

    public async DurableTask<int> AwaitDetachedEchoesAsync(Guid workerKey, int value)
    {
        var worker = GrainFactory.GetGrain<IDurableTaskWorkerGrain>(workerKey);
        var awaited = await worker.EchoAsync(value).Detached();
        var scheduled = await worker.EchoAsync(value + 1).Detached().WithId("scheduled-echo").ScheduleAsync();
        return awaited + await scheduled;
    }

    public async DurableTask<string> ScheduleConflictingEchoAsync(Guid workerKey, string rootId)
    {
        try
        {
            await GrainFactory.GetGrain<IDurableTaskWorkerGrain>(workerKey).EchoAsync(2).Detached().WithId(rootId).ScheduleAsync();
            return "accepted";
        }
        catch (DurableTaskConflictException conflict)
        {
            return conflict.Message;
        }
    }

    public DurableTask<int> WaitForCancellationAsync() =>
        DurableTask.Run(async cancellationToken =>
        {
            probe.RecordStarted(this.GetPrimaryKey());
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        });

    public async Task<DurableTaskStatus> CancelThroughManagerAsync(string rootId)
    {
        var taskId = TaskId.CreateRoot(rootId);
        await tasks.CancelAsync(taskId);
        return (await tasks.GetTask<int>(taskId).GetResponseAsync()).Status;
    }
}

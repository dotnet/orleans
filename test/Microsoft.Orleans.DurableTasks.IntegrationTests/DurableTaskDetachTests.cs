using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Journaling;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>
/// Exercises detached tasks across grains and in-grain task management through <see cref="IDurableTaskManager"/>.
/// </summary>
public abstract class DurableTaskDetachTests(string journalFormatKey) : IAsyncLifetime
{
    private readonly DurableTaskRecoveryFixture _fixture = new(journalFormatKey);

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task OrdinaryGrainMethodDetachesRootWhichIsPolledById()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var starter = _fixture.Client.GetGrain<IDurableTaskDetachStarterGrain>(Guid.NewGuid());
        var workerKey = Guid.NewGuid();
        var started = _fixture.MessagingProbe.WaitForStartedAsync(workerKey, 1);

        await starter.DetachEchoAsync(workerKey, 21, "detached-echo");
        await started.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        var detached = _fixture.Client.GetGrain<IDurableTaskWorkerGrain>(workerKey).GetDurableTask<int>("detached-echo");
        var response = await detached.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(42, response.GetResult<int>());

        // The starter retains no task for the detached call.
        Assert.Empty(await GetTaskIdsAsync(starter, cancellationToken));
    }

    [Fact]
    public async Task WorkflowDetachesRootAndRetainsOnlyItsMarker()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var starter = _fixture.Client.GetGrain<IDurableTaskDetachStarterGrain>(Guid.NewGuid());
        var workerKey = Guid.NewGuid();
        var started = _fixture.MessagingProbe.WaitForStartedAsync(workerKey, 1);

        var workflow = await starter.DetachEchoInWorkflowAsync(workerKey, 5, "workflow-echo").ScheduleAsync("workflow", cancellationToken);
        await workflow.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await started.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        var detached = _fixture.Client.GetGrain<IDurableTaskWorkerGrain>(workerKey).GetDurableTask<int>("workflow-echo");
        var response = await detached.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(10, response.GetResult<int>());

        // The starter retains the client-scheduled workflow with the marker in its tree, but no task for the detached call.
        var starterTasks = await GetTaskIdsAsync(starter, cancellationToken);
        Assert.Equal(2, starterTasks.Count);
        Assert.Contains(TaskId.CreateRoot("workflow"), starterTasks);
        var workerId = _fixture.Client.GetGrain<IDurableTaskWorkerGrain>(workerKey).GetGrainId();
        Assert.Contains(TaskId.CreateRoot("workflow").Child($"$detach:{workerId}:workflow-echo"), starterTasks);
    }

    [Fact]
    public async Task ClientDetachesRootWhichIsPolledById()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var worker = _fixture.Client.GetGrain<IDurableTaskWorkerGrain>(Guid.NewGuid());

        await worker.EchoAsync(4).DetachAsync("client-detached", cancellationToken);

        var response = await worker.GetDurableTask<int>("client-detached").GetResponseAsync(cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(8, response.GetResult<int>());
    }

    [Fact]
    public async Task GrainCancelsItsOwnTaskThroughTheTaskManager()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var starterKey = Guid.NewGuid();
        var starter = _fixture.Client.GetGrain<IDurableTaskDetachStarterGrain>(starterKey);
        var started = _fixture.MessagingProbe.WaitForStartedAsync(starterKey, 1);
        var waiting = await starter.WaitForCancellationAsync().ScheduleAsync("wait", cancellationToken);
        await started.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        var status = await starter.CancelThroughManagerAsync("wait").WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        Assert.Equal(DurableTaskStatus.Canceled, status);
        var response = await waiting.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
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

/// <summary>Runs detached task coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskDetachTests() : DurableTaskDetachTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs detached task coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskDetachTests() : DurableTaskDetachTests(DurableTaskRecoveryFixture.OrleansBinary);

public interface IDurableTaskDetachStarterGrain : IGrainWithGuidKey
{
    Task DetachEchoAsync(Guid workerKey, int value, string rootId);

    DurableTask DetachEchoInWorkflowAsync(Guid workerKey, int value, string rootId);

    DurableTask<int> WaitForCancellationAsync();

    Task<DurableTaskStatus> CancelThroughManagerAsync(string rootId);
}

[GrainType("durable-task-detach-starter-test")]
public sealed class DurableTaskDetachStarterGrain(IDurableTaskManager tasks, DurableTaskMessagingProbe probe)
    : DurableGrain, IDurableTaskDetachStarterGrain
{
    public async Task DetachEchoAsync(Guid workerKey, int value, string rootId) =>
        await GrainFactory.GetGrain<IDurableTaskWorkerGrain>(workerKey).EchoAsync(value).DetachAsync(rootId);

    public async DurableTask DetachEchoInWorkflowAsync(Guid workerKey, int value, string rootId) =>
        await GrainFactory.GetGrain<IDurableTaskWorkerGrain>(workerKey).EchoAsync(value).DetachAsync(rootId);

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

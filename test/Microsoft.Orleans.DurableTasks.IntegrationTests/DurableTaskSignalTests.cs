using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Journaling;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>Exercises signal delivery from clients, durable executions on other grains, and a grain's own methods.</summary>
public abstract class DurableTaskSignalTests(string journalFormatKey) : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly DurableTaskRecoveryFixture _fixture = new(journalFormatKey);

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task ClientSignalsWaitingWorkflowAndConflictingSignalsAreRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var waiterKey = Guid.NewGuid();
        var waiter = _fixture.Client.GetGrain<IDurableTaskSignalWaiterGrain>(waiterKey);
        var taskId = TaskId.CreateRoot("approval");
        var started = _fixture.MessagingProbe.WaitForStartedAsync(waiterKey, 1);
        var workflow = await waiter.WaitForApprovalAsync().ScheduleAsync("approval", cancellationToken);
        await started.WaitAsync(Timeout, cancellationToken);

        await waiter.SignalDurableTaskAsync(taskId, DurableTaskSignalWaiterGrain.Approved, "ada", cancellationToken);

        Assert.Equal("ada", await GetResultAsync(workflow, cancellationToken));
        // Root ID overloads address TaskId.CreateRoot(rootId).
        await waiter.SignalDurableTaskAsync("approval", DurableTaskSignalWaiterGrain.Approved, "ada", cancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => waiter.SignalDurableTaskAsync(taskId, DurableTaskSignalWaiterGrain.Approved, "grace", cancellationToken).AsTask());
        await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            () => waiter.SignalDurableTaskAsync("missing", DurableTaskSignalWaiterGrain.Approved, "ada", cancellationToken).AsTask());

        // The workflow's tree holds the signal's record with its value.
        var tasks = await GetTasksAsync(waiter, cancellationToken);
        Assert.Equal("ada", tasks[taskId.Child("$signal:approved")].Response);
    }

    [Fact]
    public async Task WorkflowOnAnotherGrainSignalsThroughTheOutbox()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var waiterKey = Guid.NewGuid();
        var waiter = _fixture.Client.GetGrain<IDurableTaskSignalWaiterGrain>(waiterKey);
        var sender = _fixture.Client.GetGrain<IDurableTaskSignalSenderGrain>(Guid.NewGuid());
        var started = _fixture.MessagingProbe.WaitForStartedAsync(waiterKey, 1);
        var workflow = await waiter.WaitForApprovalAsync().ScheduleAsync("approval", cancellationToken);
        await started.WaitAsync(Timeout, cancellationToken);

        var sending = await sender.SendApprovalAsync(waiterKey, "approval", "grace").ScheduleAsync("send", cancellationToken);

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, (await sending.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken)).Status);
        Assert.Equal("grace", await GetResultAsync(workflow, cancellationToken));

        // The sender records the send in its workflow's tree, so a replay doesn't send it again.
        var senderTasks = await GetTasksAsync(sender, cancellationToken);
        Assert.Equal(
            [TaskId.CreateRoot("send"), TaskId.CreateRoot("send").Child("$child-1")],
            senderTasks.Keys.OrderBy(static id => id.ToString(), StringComparer.Ordinal));
    }

    [Fact]
    public async Task GrainSignalsItsOwnWorkflowAndReplayObservesTheRecordedValue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var waiterKey = Guid.NewGuid();
        var waiter = _fixture.Client.GetGrain<IDurableTaskSignalWaiterGrain>(waiterKey);
        var started = _fixture.MessagingProbe.WaitForStartedAsync(waiterKey, 1);
        var workflow = await waiter.WaitForTwoApprovalsAsync().ScheduleAsync("approvals", cancellationToken);
        await started.WaitAsync(Timeout, cancellationToken);

        await waiter.ApproveAsync("approvals", "ada");
        await waiter.DeactivateAsync();
        var replayed = _fixture.MessagingProbe.WaitForStartedAsync(waiterKey, 2);
        await waiter.SignalDurableTaskAsync(
            TaskId.CreateRoot("approvals"),
            DurableTaskSignalWaiterGrain.Approved.For("second"),
            "grace",
            cancellationToken);

        Assert.Equal("ada+grace", await GetResultAsync(workflow, cancellationToken));
        await replayed.WaitAsync(Timeout, cancellationToken);
    }

    private static async Task<T> GetResultAsync<T>(ScheduledTask<T> task, CancellationToken cancellationToken) =>
        (await task.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken)).GetResult<T>();

    private static async Task<Dictionary<TaskId, DurableTaskDiagnosticState>> GetTasksAsync(IAddressable grain, CancellationToken cancellationToken)
    {
        var tasks = new Dictionary<TaskId, DurableTaskDiagnosticState>();
        await foreach (var (taskId, state) in grain.AsReference<IDurableTaskGrainExtension>().GetTasksAsync(cancellationToken))
        {
            tasks.Add(taskId, state);
        }

        return tasks;
    }
}

/// <summary>Runs signal coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskSignalTests() : DurableTaskSignalTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs signal coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskSignalTests() : DurableTaskSignalTests(DurableTaskRecoveryFixture.OrleansBinary);

public interface IDurableTaskSignalWaiterGrain : IGrainWithGuidKey
{
    DurableTask<string> WaitForApprovalAsync();

    DurableTask<string> WaitForTwoApprovalsAsync();

    Task ApproveAsync(string rootId, string value);

    Task DeactivateAsync();
}

public interface IDurableTaskSignalSenderGrain : IGrainWithGuidKey
{
    DurableTask SendApprovalAsync(Guid waiterKey, string rootId, string value);
}

[GrainType("durable-task-signal-waiter-test")]
public sealed class DurableTaskSignalWaiterGrain(IDurableTaskManager tasks, DurableTaskMessagingProbe probe)
    : DurableGrain, IDurableTaskSignalWaiterGrain
{
    public static readonly DurableSignal<string> Approved = new("approved");

    public async DurableTask<string> WaitForApprovalAsync()
    {
        probe.RecordStarted(this.GetPrimaryKey());
        return await Approved.WaitAsync();
    }

    public async DurableTask<string> WaitForTwoApprovalsAsync()
    {
        probe.RecordStarted(this.GetPrimaryKey());
        var first = await Approved.WaitAsync();
        return $"{first}+{await Approved.For("second").WaitAsync()}";
    }

    public async Task ApproveAsync(string rootId, string value) =>
        await tasks.SignalAsync(TaskId.CreateRoot(rootId), Approved, value);

    public Task DeactivateAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }
}

[GrainType("durable-task-signal-sender-test")]
public sealed class DurableTaskSignalSenderGrain : DurableGrain, IDurableTaskSignalSenderGrain
{
    public async DurableTask SendApprovalAsync(Guid waiterKey, string rootId, string value) =>
        await GrainFactory.GetGrain<IDurableTaskSignalWaiterGrain>(waiterKey)
            .SignalDurableTaskAsync(TaskId.CreateRoot(rootId), DurableTaskSignalWaiterGrain.Approved, value);
}

using Orleans.DurableTasks;
using Orleans.Journaling;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>Exercises cancellation reasons, inline compensation with suppressed cancellation, and signal results across grains.</summary>
public abstract class DurableTaskCancellationReasonTests(string journalFormatKey) : IAsyncLifetime
{
    private readonly DurableTaskRecoveryFixture _fixture = new(journalFormatKey);

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task CancellationReasonReachesTheRemoteChildOfAWorkflow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var holderKey = Guid.NewGuid();
        var workflow = _fixture.Client.GetGrain<IDurableTaskReasonWorkflowGrain>(Guid.NewGuid());
        var holding = _fixture.MessagingProbe.WaitForStartedAsync(holderKey, 1);

        var scheduled = await workflow.RunAsync(holderKey).WithId("reason").ScheduleAsync(cancellationToken);
        await holding.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await scheduled.CancelAsync("Cancelled by the buyer", cancellationToken);

        var response = await scheduled.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal("workflow: Cancelled by the buyer; holder: Cancelled by the buyer", response.GetResult<string>());
    }

    [Fact]
    public async Task CanceledWorkflowCompensatesInlineThroughASuppressedRemoteCall()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var holderKey = Guid.NewGuid();
        var workflow = _fixture.Client.GetGrain<IDurableTaskReasonWorkflowGrain>(Guid.NewGuid());
        var blocking = _fixture.MessagingProbe.WaitForStartedAsync(holderKey, 1);

        var scheduled = await workflow.CompensateInlineAsync(holderKey).WithId("compensate").ScheduleAsync(cancellationToken);
        await blocking.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await scheduled.CancelAsync("Cancelled by the buyer", cancellationToken);

        var response = await scheduled.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal("released 2 after Cancelled by the buyer", response.GetResult<string>());
    }

    [Fact]
    public async Task ClientTrySignalReportsAnUnknownTask()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workflow = _fixture.Client.GetGrain<IDurableTaskReasonWorkflowGrain>(Guid.NewGuid());

        var result = await workflow.TrySignalDurableTaskAsync(
            "missing",
            new DurableSignal<string>("shipped"),
            "parcel",
            cancellationToken);

        Assert.Equal(DurableSignalResult.TaskNotFound, result);
    }
}

/// <summary>Runs cancellation reason coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskCancellationReasonTests() : DurableTaskCancellationReasonTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs cancellation reason coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskCancellationReasonTests() : DurableTaskCancellationReasonTests(DurableTaskRecoveryFixture.OrleansBinary);

public interface IDurableTaskReasonWorkflowGrain : IGrainWithGuidKey
{
    DurableTask<string> RunAsync(Guid holderKey);

    DurableTask<string> CompensateInlineAsync(Guid holderKey);
}

public interface IDurableTaskReasonHolderGrain : IGrainWithGuidKey
{
    DurableTask<string> HoldAsync();

    DurableTask BlockAsync();

    DurableTask<int> ReleaseAsync(int count);
}

[GrainType("durable-task-reason-workflow-test")]
public sealed class DurableTaskReasonWorkflowGrain : DurableGrain, IDurableTaskReasonWorkflowGrain
{
    public async DurableTask<string> RunAsync(Guid holderKey)
    {
        var holder = await GrainFactory.GetGrain<IDurableTaskReasonHolderGrain>(holderKey).HoldAsync().WithId("hold");
        return $"workflow: {DurableTask.Current.TaskCancellationReason}; holder: {holder}";
    }

    public async DurableTask<string> CompensateInlineAsync(Guid holderKey)
    {
        var holder = GrainFactory.GetGrain<IDurableTaskReasonHolderGrain>(holderKey);
        try
        {
            await holder.BlockAsync().WithId("block");
            return "completed";
        }
        catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
        {
            var released = await holder.ReleaseAsync(2).WithId("release").Shielded();
            return $"released {released} after {DurableTask.Current.TaskCancellationReason}";
        }
    }
}

[GrainType("durable-task-reason-holder-test")]
public sealed class DurableTaskReasonHolderGrain(DurableTaskMessagingProbe probe) : DurableGrain, IDurableTaskReasonHolderGrain
{
    public DurableTask<string> HoldAsync() =>
        DurableTask.Run(async cancellationToken =>
        {
            probe.RecordStarted(this.GetPrimaryKey());
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "completed";
            }
            catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
            {
                return DurableTask.Current.TaskCancellationReason!;
            }
        });

    public DurableTask BlockAsync() =>
        DurableTask.Run(async cancellationToken =>
        {
            probe.RecordStarted(this.GetPrimaryKey());
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });

    public DurableTask<int> ReleaseAsync(int count) => DurableTask.FromResult(count);
}

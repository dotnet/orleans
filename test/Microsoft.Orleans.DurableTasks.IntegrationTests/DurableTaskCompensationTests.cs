using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Journaling;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>
/// Exercises cooperative cancellation with compensation through a detached task, and administrative abort, across grains.
/// </summary>
public abstract class DurableTaskCompensationTests(string journalFormatKey) : IAsyncLifetime
{
    private readonly DurableTaskRecoveryFixture _fixture = new(journalFormatKey);

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task CanceledWorkflowCompensatesThroughDetachedTaskAndReturnsItsOwnResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workflowKey = Guid.NewGuid();
        var inventoryKey = Guid.NewGuid();
        var workflow = _fixture.Client.GetGrain<IDurableTaskCompensatingWorkflowGrain>(workflowKey);
        var holding = _fixture.MessagingProbe.WaitForStartedAsync(inventoryKey, 1);
        var holdCanceled = _fixture.MessagingProbe.WaitForCanceledAsync(inventoryKey, 1);

        var scheduled = await workflow.ReserveAsync(inventoryKey, 3).ScheduleAsync("reserve", cancellationToken);
        await holding.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await scheduled.CancelAsync(cancellationToken);

        var response = await scheduled.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal("compensated", response.GetResult<string>());
        await holdCanceled.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        // The compensation is a detached root on the inventory grain, which runs to completion after the cancellation.
        var release = _fixture.Client.GetGrain<IDurableTaskInventoryTestGrain>(inventoryKey)
            .GetDurableTask<int>(DurableTaskCompensatingWorkflowGrain.GetCompensationRootId(workflowKey));
        var released = await release.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(3, released.GetResult<int>());
    }

    [Fact]
    public async Task AbortThroughGrainExtensionFaultsWorkflowAndAbortsRemoteChildWithoutCompensation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workflowKey = Guid.NewGuid();
        var inventoryKey = Guid.NewGuid();
        var workflow = _fixture.Client.GetGrain<IDurableTaskCompensatingWorkflowGrain>(workflowKey);
        var holding = _fixture.MessagingProbe.WaitForStartedAsync(inventoryKey, 1);
        var holdStopped = _fixture.MessagingProbe.WaitForCanceledAsync(inventoryKey, 1);
        var scheduled = await workflow.ReserveAsync(inventoryKey, 2).ScheduleAsync("abort-me", cancellationToken);
        await holding.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        await workflow.AsReference<IDurableTaskGrainExtension>().AbortAsync(TaskId.CreateRoot("abort-me"), "operator", cancellationToken);

        var response = await scheduled.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(DurableTaskStatus.Failed, response.Status);
        Assert.Equal("operator", Assert.IsType<DurableTaskAbortedException>(response.Exception).Message);
        await holdStopped.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        var diagnostics = await GetDiagnosticsAsync(workflow, TaskId.CreateRoot("abort-me"), cancellationToken);
        Assert.Equal("Faulted", diagnostics.Status);
        Assert.Equal("DurableTaskAbortedException: operator", diagnostics.Response);

        // Abort runs no workflow code, so no compensation started.
        var release = _fixture.Client.GetGrain<IDurableTaskInventoryTestGrain>(inventoryKey)
            .GetDurableTask<int>(DurableTaskCompensatingWorkflowGrain.GetCompensationRootId(workflowKey));
        await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            () => release.GetResponseAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, cancellationToken));
    }

    private static async Task<DurableTaskDiagnosticState> GetDiagnosticsAsync(
        IAddressable grain,
        TaskId taskId,
        CancellationToken cancellationToken)
    {
        await foreach (var (id, state) in grain.AsReference<IDurableTaskGrainExtension>().GetTasksAsync(cancellationToken))
        {
            if (id == taskId)
            {
                return state;
            }
        }

        throw new Xunit.Sdk.XunitException($"Task '{taskId}' has no diagnostics.");
    }
}

/// <summary>Runs compensation and abort coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskCompensationTests() : DurableTaskCompensationTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs compensation and abort coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskCompensationTests() : DurableTaskCompensationTests(DurableTaskRecoveryFixture.OrleansBinary);

public interface IDurableTaskCompensatingWorkflowGrain : IGrainWithGuidKey
{
    DurableTask<string> ReserveAsync(Guid inventoryKey, int count);
}

public interface IDurableTaskInventoryTestGrain : IGrainWithGuidKey
{
    DurableTask<int> ReserveAsync(int item);

    DurableTask<int> HoldAsync();

    DurableTask<int> ReleaseAsync(int count);
}

[GrainType("durable-task-compensating-workflow-test")]
public sealed class DurableTaskCompensatingWorkflowGrain : DurableGrain, IDurableTaskCompensatingWorkflowGrain
{
    public static string GetCompensationRootId(Guid workflowKey) => $"compensate-{workflowKey:N}";

    public async DurableTask<string> ReserveAsync(Guid inventoryKey, int count)
    {
        var inventory = GrainFactory.GetGrain<IDurableTaskInventoryTestGrain>(inventoryKey);
        var reserved = 0;
        try
        {
            for (var item = 0; item < count; item++)
            {
                await inventory.ReserveAsync(item).WithId($"reserve-{item}");
                reserved++;
            }

            await inventory.HoldAsync().WithId("hold");
            return "completed";
        }
        catch (OperationCanceledException) when (DurableExecutionContext.Current!.IsCancellationRequested)
        {
            await inventory.ReleaseAsync(reserved).DetachAsync(GetCompensationRootId(this.GetPrimaryKey()));
            return "compensated";
        }
    }
}

[GrainType("durable-task-inventory-test")]
public sealed class DurableTaskInventoryTestGrain(DurableTaskMessagingProbe probe) : DurableGrain, IDurableTaskInventoryTestGrain
{
    public DurableTask<int> ReserveAsync(int item) => DurableTask.FromResult(item);

    public DurableTask<int> HoldAsync() =>
        DurableTask.Run(async cancellationToken =>
        {
            probe.RecordStarted(this.GetPrimaryKey());
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                probe.RecordCanceled(this.GetPrimaryKey());
                throw;
            }

            return 0;
        });

    public DurableTask<int> ReleaseAsync(int count) => DurableTask.FromResult(count);
}

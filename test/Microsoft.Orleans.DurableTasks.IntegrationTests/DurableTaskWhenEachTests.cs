using System.Collections.Concurrent;
using Orleans.DurableTasks;
using Orleans.Journaling;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>Verifies that a workflow's <c>WhenEach</c> yields the same completion order in every activation.</summary>
public abstract class DurableTaskWhenEachTests(string journalFormatKey) : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly DurableTaskRecoveryFixture _fixture = new(journalFormatKey);

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task WhenEachYieldsTheSameOrderAcrossDeactivations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var key = Guid.NewGuid();
        var yieldKey = Guid.NewGuid();
        var grain = _fixture.Client.GetGrain<IDurableTaskWhenEachGrain>(key);
        var taskId = TaskId.CreateRoot("each");
        var started = _fixture.MessagingProbe.WaitForStartedAsync(key, 1);
        var workflow = await grain.CollectInCompletionOrderAsync(yieldKey).WithId("each").ScheduleAsync(cancellationToken);
        await started.WaitAsync(Timeout, cancellationToken);

        // Input 2 completes first, then the activation is deactivated.
        await SignalAsync(grain, taskId, "2", cancellationToken);
        await _fixture.MessagingProbe.WaitForStartedAsync(yieldKey, 1).WaitAsync(Timeout, cancellationToken);
        await grain.DeactivateAsync();

        // The next activation replays input 2's completion, then observes inputs 0 and 1 complete in that order.
        await SignalAsync(grain, taskId, "0", cancellationToken);
        await _fixture.MessagingProbe.WaitForStartedAsync(yieldKey, 3).WaitAsync(Timeout, cancellationToken);
        await SignalAsync(grain, taskId, "1", cancellationToken);

        // The attempt records its order once the enumeration ends.
        await _fixture.MessagingProbe.WaitForStartedAsync(yieldKey, 5).WaitAsync(Timeout, cancellationToken);
        await grain.DeactivateAsync();

        // The last activation replays the enumeration with every input complete.
        await SignalAsync(grain, taskId, "done", cancellationToken);

        var response = await workflow.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);
        Assert.Equal("2,0,1", response.GetResult<string>());
        Assert.Equal(["2,0,1", "2,0,1"], DurableTaskWhenEachGrain.Orders[key]);
        Assert.Equal(3, _fixture.MessagingProbe.GetStartedCount(key));
    }

    private static ValueTask SignalAsync(IDurableTaskWhenEachGrain grain, TaskId taskId, string item, CancellationToken cancellationToken) =>
        grain.SignalDurableTaskAsync(taskId, DurableTaskWhenEachGrain.Item.For(item), item, cancellationToken);
}

/// <summary>Runs <c>WhenEach</c> coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskWhenEachTests() : DurableTaskWhenEachTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs <c>WhenEach</c> coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskWhenEachTests() : DurableTaskWhenEachTests(DurableTaskRecoveryFixture.OrleansBinary);

public interface IDurableTaskWhenEachGrain : IGrainWithGuidKey
{
    DurableTask<string> CollectInCompletionOrderAsync(Guid yieldKey);

    Task DeactivateAsync();
}

[GrainType("durable-task-when-each-test")]
public sealed class DurableTaskWhenEachGrain(DurableTaskMessagingProbe probe) : DurableGrain, IDurableTaskWhenEachGrain
{
    public static readonly DurableSignal<string> Item = new("item");

    /// <summary>Gets the order which each attempt which finished the enumeration observed, by grain key.</summary>
    public static ConcurrentDictionary<Guid, ConcurrentQueue<string>> Orders { get; } = [];

    public async DurableTask<string> CollectInCompletionOrderAsync(Guid yieldKey)
    {
        probe.RecordStarted(this.GetPrimaryKey());
        var order = new List<string>();
        await foreach (var item in DurableTask.WhenEach([Item.For("0").WaitAsync(), Item.For("1").WaitAsync(), Item.For("2").WaitAsync()]))
        {
            order.Add(item.GetResult());
            probe.RecordStarted(yieldKey);
        }

        var joined = string.Join(",", order);
        Orders.GetOrAdd(this.GetPrimaryKey(), static _ => []).Enqueue(joined);
        probe.RecordStarted(yieldKey);
        await Item.For("done").WaitAsync();
        return joined;
    }

    public Task DeactivateAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }
}

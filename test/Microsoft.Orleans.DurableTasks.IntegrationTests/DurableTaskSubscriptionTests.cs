using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Journaling;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>
/// Exercises durable executions which subscribe through GetDurableTask to the terminal result of tasks which they didn't
/// invoke, on other grains and on their own grain.
/// </summary>
public abstract class DurableTaskSubscriptionTests(string journalFormatKey) : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly DurableTaskRecoveryFixture _fixture = new(journalFormatKey);

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task WorkflowSubscribesToAClientScheduledTaskOnAnotherGrain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (operationKey, operation) = await StartOperationAsync("op", cancellationToken);
        var subscriberKey = Guid.NewGuid();
        var subscriber = _fixture.Client.GetGrain<IDurableTaskSubscriberGrain>(subscriberKey);
        var subscribing = _fixture.MessagingProbe.WaitForStartedAsync(subscriberKey, 1);

        var relay = await subscriber.SubscribeAsync(operationKey, "op").WithId("relay").ScheduleAsync(cancellationToken);
        await subscribing.WaitAsync(Timeout, cancellationToken);
        await operation.SignalDurableTaskAsync("op", DurableTaskSubscribedGrain.Release, "done", cancellationToken);

        Assert.Equal("done", await GetResultAsync(relay, cancellationToken));

        // The client which scheduled the operation still polls its retained result, and awaiting a reference outside a
        // durable execution waits for it.
        Assert.Equal("done", await GetResultAsync(operation.GetDurableTask<string>("op"), cancellationToken));
        Assert.Equal("done", await operation.GetDurableTask<string>("op"));

        // The relay's tree records the subscription as a child.
        var subscription = Assert.Single(
            await GetTasksAsync(subscriber, cancellationToken),
            task => task.Key != TaskId.CreateRoot("relay"));
        Assert.Equal(TaskId.CreateRoot("relay"), subscription.Key.Parent());
        Assert.Equal($"a subscription to task 'op' on grain '{operation.GetGrainId()}'", subscription.Value.Request);
        Assert.Equal("Completed", subscription.Value.Status);
    }

    [Fact]
    public async Task SubscriptionToACompletedTaskCompletesAndToAnUnknownTaskFailsPromptly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (operationKey, operation) = await StartOperationAsync("op", cancellationToken);
        await operation.SignalDurableTaskAsync("op", DurableTaskSubscribedGrain.Release, "done", cancellationToken);
        Assert.Equal("done", await GetResultAsync(operation.GetDurableTask<string>("op"), cancellationToken));
        var subscriber = _fixture.Client.GetGrain<IDurableTaskSubscriberGrain>(Guid.NewGuid());

        var completed = await subscriber.SubscribeAsync(operationKey, "op").WithId("completed").ScheduleAsync(cancellationToken);
        var unknown = await subscriber.SubscribeAsync(operationKey, "unknown").WithId("unknown").ScheduleAsync(cancellationToken);

        Assert.Equal("done", await GetResultAsync(completed, cancellationToken));
        Assert.Equal("not found: unknown", await GetResultAsync(unknown, cancellationToken));
    }

    [Fact]
    public async Task WorkflowSubscribesToTheDetachedTaskWhichItsScheduleReturned()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var operationKey = Guid.NewGuid();
        var operation = _fixture.Client.GetGrain<IDurableTaskSubscribedGrain>(operationKey);
        var subscriber = _fixture.Client.GetGrain<IDurableTaskSubscriberGrain>(Guid.NewGuid());
        var started = _fixture.MessagingProbe.WaitForStartedAsync(operationKey, 1);

        // The schedule returns once the operation's grain holds the task, so the subscription which follows finds it.
        var relay = await subscriber.ScheduleAndSubscribeAsync(operationKey, "detached-op").WithId("relay").ScheduleAsync(cancellationToken);
        await started.WaitAsync(Timeout, cancellationToken);
        await operation.SignalDurableTaskAsync("detached-op", DurableTaskSubscribedGrain.Release, "done", cancellationToken);

        Assert.Equal("done", await GetResultAsync(relay, cancellationToken));
    }

    [Fact]
    public async Task DeadlineEndsTheSubscriptionAndTheTaskContinues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (operationKey, operation) = await StartOperationAsync("op", cancellationToken);
        var subscriberKey = Guid.NewGuid();
        var subscriber = _fixture.Client.GetGrain<IDurableTaskSubscriberGrain>(subscriberKey);
        var subscribing = _fixture.MessagingProbe.WaitForStartedAsync(subscriberKey, 1);
        var relay = await subscriber.SubscribeWithDeadlineAsync(operationKey, "op").WithId("relay").ScheduleAsync(cancellationToken);
        await subscribing.WaitAsync(Timeout, cancellationToken);

        // The deadline's delay is due once the Durable Jobs clock passes it.
        _fixture.JobClock.Advance(TimeSpan.FromHours(2));

        Assert.Equal("timed out", await GetResultAsync(relay, cancellationToken));
        var pending = await operation.GetDurableTask<string>("op").GetStatusAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, cancellationToken);
        Assert.Equal(DurableTaskStatus.Pending, pending);
        await operation.SignalDurableTaskAsync("op", DurableTaskSubscribedGrain.Release, "done", cancellationToken);
        Assert.Equal("done", await GetResultAsync(operation.GetDurableTask<string>("op"), cancellationToken));
    }

    [Fact]
    public async Task WorkflowSubscribesToATaskOfItsOwnGrain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (operationKey, operation) = await StartOperationAsync("op", cancellationToken);
        var relay = await operation.SubscribeToOwnAsync("op").WithId("relay").ScheduleAsync(cancellationToken);
        await _fixture.MessagingProbe.WaitForStartedAsync(operationKey, 2).WaitAsync(Timeout, cancellationToken);

        await operation.SignalDurableTaskAsync("op", DurableTaskSubscribedGrain.Release, "done", cancellationToken);

        Assert.Equal("done", await GetResultAsync(relay, cancellationToken));
    }

    [Fact]
    public async Task SubscriberWhichDeactivatesReceivesTheResultInItsNextActivation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (operationKey, operation) = await StartOperationAsync("op", cancellationToken);
        var subscriberKey = Guid.NewGuid();
        var subscriber = _fixture.Client.GetGrain<IDurableTaskSubscriberGrain>(subscriberKey);
        var subscribing = _fixture.MessagingProbe.WaitForStartedAsync(subscriberKey, 1);
        var relay = await subscriber.SubscribeAsync(operationKey, "op").WithId("relay").ScheduleAsync(cancellationToken);
        await subscribing.WaitAsync(Timeout, cancellationToken);

        await subscriber.DeactivateAsync();
        var replayed = _fixture.MessagingProbe.WaitForStartedAsync(subscriberKey, 2);
        await operation.SignalDurableTaskAsync("op", DurableTaskSubscribedGrain.Release, "done", cancellationToken);

        Assert.Equal("done", await GetResultAsync(relay, cancellationToken));
        await replayed.WaitAsync(Timeout, cancellationToken);
    }

    private async Task<(Guid Key, IDurableTaskSubscribedGrain Grain)> StartOperationAsync(string rootId, CancellationToken cancellationToken)
    {
        var key = Guid.NewGuid();
        var grain = _fixture.Client.GetGrain<IDurableTaskSubscribedGrain>(key);
        var started = _fixture.MessagingProbe.WaitForStartedAsync(key, 1);
        await grain.RunAsync().WithId(rootId).ScheduleAsync(cancellationToken);
        await started.WaitAsync(Timeout, cancellationToken);
        return (key, grain);
    }

    private static async Task<T> GetResultAsync<T>(ScheduledTask<T> task, CancellationToken cancellationToken) =>
        (await task.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken)).GetResult<T>();

    private static async Task<T> GetResultAsync<T>(DurableTaskReference<T> task, CancellationToken cancellationToken) =>
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

/// <summary>Runs subscription coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskSubscriptionTests() : DurableTaskSubscriptionTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs subscription coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskSubscriptionTests() : DurableTaskSubscriptionTests(DurableTaskRecoveryFixture.OrleansBinary);

public interface IDurableTaskSubscribedGrain : IGrainWithGuidKey
{
    DurableTask<string> RunAsync();

    DurableTask<string> SubscribeToOwnAsync(string rootId);
}

public interface IDurableTaskSubscriberGrain : IGrainWithGuidKey
{
    DurableTask<string> SubscribeAsync(Guid operationKey, string rootId);

    DurableTask<string> SubscribeWithDeadlineAsync(Guid operationKey, string rootId);

    DurableTask<string> ScheduleAndSubscribeAsync(Guid operationKey, string rootId);

    Task DeactivateAsync();
}

[GrainType("durable-task-subscribed-test")]
public sealed class DurableTaskSubscribedGrain(DurableTaskMessagingProbe probe) : DurableGrain, IDurableTaskSubscribedGrain
{
    public static readonly DurableSignal<string> Release = new("release");

    public async DurableTask<string> RunAsync()
    {
        probe.RecordStarted(this.GetPrimaryKey());
        return await Release.WaitAsync();
    }

    public async DurableTask<string> SubscribeToOwnAsync(string rootId)
    {
        probe.RecordStarted(this.GetPrimaryKey());
        return await this.GetDurableTask<string>(rootId);
    }
}

[GrainType("durable-task-subscriber-test")]
public sealed class DurableTaskSubscriberGrain(DurableTaskMessagingProbe probe) : DurableGrain, IDurableTaskSubscriberGrain
{
    public async DurableTask<string> SubscribeAsync(Guid operationKey, string rootId)
    {
        probe.RecordStarted(this.GetPrimaryKey());
        try
        {
            return await GrainFactory.GetGrain<IDurableTaskSubscribedGrain>(operationKey).GetDurableTask<string>(rootId);
        }
        catch (DurableTaskNotFoundException exception)
        {
            return $"not found: {exception.TaskId}";
        }
    }

    public async DurableTask<string> SubscribeWithDeadlineAsync(Guid operationKey, string rootId)
    {
        probe.RecordStarted(this.GetPrimaryKey());
        try
        {
            return await GrainFactory.GetGrain<IDurableTaskSubscribedGrain>(operationKey)
                .GetDurableTask<string>(rootId)
                .WaitAsync(TimeSpan.FromHours(1));
        }
        catch (TimeoutException)
        {
            return "timed out";
        }
    }

    public async DurableTask<string> ScheduleAndSubscribeAsync(Guid operationKey, string rootId)
    {
        var operation = GrainFactory.GetGrain<IDurableTaskSubscribedGrain>(operationKey);
        await operation.RunAsync().Detached().WithId(rootId).ScheduleAsync();
        return await operation.GetDurableTask<string>(rootId);
    }

    public Task DeactivateAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }
}

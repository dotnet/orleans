using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Journaling;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>
/// Exercises durable executions which await the terminal result of tasks which they didn't invoke, on other grains and
/// on their own grain.
/// </summary>
public abstract class DurableTaskObservationTests(string journalFormatKey) : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly DurableTaskRecoveryFixture _fixture = new(journalFormatKey);

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task WorkflowObservesAClientScheduledTaskOnAnotherGrain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (operationKey, operation) = await StartOperationAsync("op", cancellationToken);
        var observerKey = Guid.NewGuid();
        var observer = _fixture.Client.GetGrain<IDurableTaskObserverGrain>(observerKey);
        var observing = _fixture.MessagingProbe.WaitForStartedAsync(observerKey, 1);

        var relay = await observer.ObserveAsync(operationKey, "op").ScheduleAsync("relay", cancellationToken);
        await observing.WaitAsync(Timeout, cancellationToken);
        await operation.SignalDurableTaskAsync("op", DurableTaskObservedGrain.Release, "done", cancellationToken);

        Assert.Equal("done", await GetResultAsync(relay, cancellationToken));

        // The client which scheduled the operation still polls its retained result.
        Assert.Equal("done", await GetResultAsync(operation.GetDurableTask<string>("op"), cancellationToken));

        // The relay's tree records the observation as a child.
        var observation = Assert.Single(
            await GetTasksAsync(observer, cancellationToken),
            task => task.Key != TaskId.CreateRoot("relay"));
        Assert.Equal(TaskId.CreateRoot("relay"), observation.Key.Parent());
        Assert.Equal($"an observation of task 'op' on grain '{operation.GetGrainId()}'", observation.Value.Request);
        Assert.Equal("Completed", observation.Value.Status);
    }

    [Fact]
    public async Task ObservationOfACompletedTaskCompletesAndOfAnUnscheduledTaskAwaitsItsInvocation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (operationKey, operation) = await StartOperationAsync("op", cancellationToken);
        await operation.SignalDurableTaskAsync("op", DurableTaskObservedGrain.Release, "done", cancellationToken);
        Assert.Equal("done", await GetResultAsync(operation.GetDurableTask<string>("op"), cancellationToken));
        var observerKey = Guid.NewGuid();
        var observer = _fixture.Client.GetGrain<IDurableTaskObserverGrain>(observerKey);
        var observing = _fixture.MessagingProbe.WaitForStartedAsync(observerKey, 2);

        var completed = await observer.ObserveAsync(operationKey, "op").ScheduleAsync("completed", cancellationToken);
        var early = await observer.ObserveAsync(operationKey, "later").ScheduleAsync("early", cancellationToken);
        Assert.Equal("done", await GetResultAsync(completed, cancellationToken));
        await observing.WaitAsync(Timeout, cancellationToken);

        // The subscription can reach the operation's grain before the task's invocation, and waits for it.
        var started = _fixture.MessagingProbe.WaitForStartedAsync(operationKey, 2);
        await operation.RunAsync().ScheduleAsync("later", cancellationToken);
        await started.WaitAsync(Timeout, cancellationToken);
        await operation.SignalDurableTaskAsync("later", DurableTaskObservedGrain.Release, "later done", cancellationToken);
        Assert.Equal("later done", await GetResultAsync(early, cancellationToken));
    }

    [Fact]
    public async Task DeadlineEndsTheObservationAndTheObservedTaskContinues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (operationKey, operation) = await StartOperationAsync("op", cancellationToken);
        var observerKey = Guid.NewGuid();
        var observer = _fixture.Client.GetGrain<IDurableTaskObserverGrain>(observerKey);
        var observing = _fixture.MessagingProbe.WaitForStartedAsync(observerKey, 1);
        var relay = await observer.ObserveWithDeadlineAsync(operationKey, "op").ScheduleAsync("relay", cancellationToken);
        await observing.WaitAsync(Timeout, cancellationToken);

        // The deadline's delay is due once the Durable Jobs clock passes it.
        _fixture.JobClock.Advance(TimeSpan.FromHours(2));

        Assert.Equal("timed out", await GetResultAsync(relay, cancellationToken));
        var pending = await operation.GetDurableTask<string>("op").GetStatusAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, cancellationToken);
        Assert.Equal(DurableTaskStatus.Pending, pending);
        await operation.SignalDurableTaskAsync("op", DurableTaskObservedGrain.Release, "done", cancellationToken);
        Assert.Equal("done", await GetResultAsync(operation.GetDurableTask<string>("op"), cancellationToken));
    }

    [Fact]
    public async Task WorkflowObservesATaskOfItsOwnGrain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (operationKey, operation) = await StartOperationAsync("op", cancellationToken);
        var relay = await operation.ObserveOwnAsync("op").ScheduleAsync("relay", cancellationToken);
        await _fixture.MessagingProbe.WaitForStartedAsync(operationKey, 2).WaitAsync(Timeout, cancellationToken);

        await operation.SignalDurableTaskAsync("op", DurableTaskObservedGrain.Release, "done", cancellationToken);

        Assert.Equal("done", await GetResultAsync(relay, cancellationToken));
    }

    [Fact]
    public async Task ObserverWhichDeactivatesReceivesTheResultInItsNextActivation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (operationKey, operation) = await StartOperationAsync("op", cancellationToken);
        var observerKey = Guid.NewGuid();
        var observer = _fixture.Client.GetGrain<IDurableTaskObserverGrain>(observerKey);
        var observing = _fixture.MessagingProbe.WaitForStartedAsync(observerKey, 1);
        var relay = await observer.ObserveAsync(operationKey, "op").ScheduleAsync("relay", cancellationToken);
        await observing.WaitAsync(Timeout, cancellationToken);

        await observer.DeactivateAsync();
        var replayed = _fixture.MessagingProbe.WaitForStartedAsync(observerKey, 2);
        await operation.SignalDurableTaskAsync("op", DurableTaskObservedGrain.Release, "done", cancellationToken);

        Assert.Equal("done", await GetResultAsync(relay, cancellationToken));
        await replayed.WaitAsync(Timeout, cancellationToken);
    }

    private async Task<(Guid Key, IDurableTaskObservedGrain Grain)> StartOperationAsync(string rootId, CancellationToken cancellationToken)
    {
        var key = Guid.NewGuid();
        var grain = _fixture.Client.GetGrain<IDurableTaskObservedGrain>(key);
        var started = _fixture.MessagingProbe.WaitForStartedAsync(key, 1);
        await grain.RunAsync().ScheduleAsync(rootId, cancellationToken);
        await started.WaitAsync(Timeout, cancellationToken);
        return (key, grain);
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

/// <summary>Runs observation coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskObservationTests() : DurableTaskObservationTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs observation coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskObservationTests() : DurableTaskObservationTests(DurableTaskRecoveryFixture.OrleansBinary);

public interface IDurableTaskObservedGrain : IGrainWithGuidKey
{
    DurableTask<string> RunAsync();

    DurableTask<string> ObserveOwnAsync(string rootId);
}

public interface IDurableTaskObserverGrain : IGrainWithGuidKey
{
    DurableTask<string> ObserveAsync(Guid operationKey, string rootId);

    DurableTask<string> ObserveWithDeadlineAsync(Guid operationKey, string rootId);

    Task DeactivateAsync();
}

[GrainType("durable-task-observed-test")]
public sealed class DurableTaskObservedGrain(DurableTaskMessagingProbe probe) : DurableGrain, IDurableTaskObservedGrain
{
    public static readonly DurableSignal<string> Release = new("release");

    public async DurableTask<string> RunAsync()
    {
        probe.RecordStarted(this.GetPrimaryKey());
        return await Release.WaitAsync();
    }

    public async DurableTask<string> ObserveOwnAsync(string rootId)
    {
        probe.RecordStarted(this.GetPrimaryKey());
        return await this.ObserveDurableTask<string>(rootId);
    }
}

[GrainType("durable-task-observer-test")]
public sealed class DurableTaskObserverGrain(DurableTaskMessagingProbe probe) : DurableGrain, IDurableTaskObserverGrain
{
    public async DurableTask<string> ObserveAsync(Guid operationKey, string rootId)
    {
        probe.RecordStarted(this.GetPrimaryKey());
        try
        {
            return await GrainFactory.GetGrain<IDurableTaskObservedGrain>(operationKey).ObserveDurableTask<string>(rootId);
        }
        catch (DurableTaskNotFoundException exception)
        {
            return $"not found: {exception.TaskId}";
        }
    }

    public async DurableTask<string> ObserveWithDeadlineAsync(Guid operationKey, string rootId)
    {
        probe.RecordStarted(this.GetPrimaryKey());
        try
        {
            return await GrainFactory.GetGrain<IDurableTaskObservedGrain>(operationKey)
                .ObserveDurableTask<string>(rootId)
                .WaitAsync(TimeSpan.FromHours(1));
        }
        catch (TimeoutException)
        {
            return "timed out";
        }
    }

    public Task DeactivateAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }
}

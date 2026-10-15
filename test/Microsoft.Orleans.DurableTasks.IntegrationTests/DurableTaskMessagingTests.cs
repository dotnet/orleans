using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Journaling;
using Orleans.Messaging;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>
/// Exercises durable grain RPC across grains over the messaging inbox and outbox.
/// </summary>
/// <remarks>
/// A coordinator fans out several durable calls to one worker, so the worker's inbox and the coordinator's inbox
/// each receive several durable RPC messages which can be processed in one inbox batch with a single batch write.
/// </remarks>
public abstract class DurableTaskMessagingTests(string journalFormatKey) : IAsyncLifetime
{
    private const int FanOut = 8;
    private readonly DurableTaskRecoveryFixture _fixture = new(journalFormatKey);

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task RemoteCompletionsReturnToCallerThroughInbox()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var coordinator = _fixture.Client.GetGrain<IDurableTaskCoordinatorGrain>(Guid.NewGuid());
        var workerKey = Guid.NewGuid();

        var scheduled = await coordinator.SumEchoesAsync(workerKey, FanOut).WithId("sum-echoes").ScheduleAsync(cancellationToken);
        var response = await scheduled.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.Equal(Enumerable.Range(0, FanOut).Sum(static value => value * 2), response.GetResult<int>());
        Assert.Equal(FanOut, _fixture.MessagingProbe.GetStartedCount(workerKey));
    }

    [Fact]
    public async Task SiloHostedCallerOutsideGrainSchedulesAndCancelsRootTasks()
    {
        // Code which runs in the silo's host but outside a grain, such as a co-hosted web endpoint, schedules through
        // the silo's grain factory like an external client.
        var cancellationToken = TestContext.Current.CancellationToken;
        var grainFactory = _fixture.Cluster.GetSiloServiceProvider().GetRequiredService<IGrainFactory>();
        var worker = grainFactory.GetGrain<IDurableTaskWorkerGrain>(Guid.NewGuid());

        var echo = await worker.EchoAsync(21).WithId("silo-hosted-echo").ScheduleAsync(cancellationToken);
        var wait = await worker.WaitForCancellationAsync(1).WithId("silo-hosted-wait").ScheduleAsync(cancellationToken);
        await wait.CancelAsync(cancellationToken);
        var canceled = await wait.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        Assert.Equal(42, await echo.WaitAsync(cancellationToken));
        Assert.Equal(DurableTaskStatus.Canceled, canceled.Status);
    }

    [Fact]
    public async Task PollingInterleavesWithOtherRequestsToTheGrain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workerKey = Guid.NewGuid();
        var worker = _fixture.Client.GetGrain<IDurableTaskWorkerGrain>(workerKey);
        var started = _fixture.MessagingProbe.WaitForStartedAsync(workerKey, 1);
        var waiting = await worker.WaitForCancellationAsync(1).WithId("long-poll").ScheduleAsync(cancellationToken);
        await started.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        // The poll outlasts the test, so a request sent after it completes only if the grain interleaves them. A waiting
        // client must not stall the grain whose requests and inbox messages complete the task.
        var poll = worker.AsReference<IDurableTaskGrainExtension>().SubscribeOrPollAsync(
            waiting.Id,
            new SubscribeOrPollOptions { PollTimeout = TimeSpan.FromMinutes(5) },
            cancellationToken).AsTask();
        var echo = await worker.EchoAsync(21).WithId("echo").ScheduleAsync(cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(42, await echo.WaitAsync(cancellationToken));
        Assert.False(poll.IsCompleted);

        await waiting.CancelAsync(cancellationToken);
        var response = await poll.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
    }
    [Fact]
    public async Task CancellationFansOutToRemoteChildrenAndCompletesThroughInbox()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var coordinator = _fixture.Client.GetGrain<IDurableTaskCoordinatorGrain>(Guid.NewGuid());
        var workerKey = Guid.NewGuid();
        var allStarted = _fixture.MessagingProbe.WaitForStartedAsync(workerKey, FanOut);
        var allCanceled = _fixture.MessagingProbe.WaitForCanceledAsync(workerKey, FanOut);

        var scheduled = await coordinator.WaitForCancellationAsync(workerKey, FanOut).WithId("fan-out").ScheduleAsync(cancellationToken);
        await allStarted.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        await scheduled.CancelAsync(cancellationToken);

        await allCanceled.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        var response = await scheduled.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        var worker = _fixture.Client.GetGrain<IDurableTaskWorkerGrain>(workerKey).AsReference<IDurableTaskGrainExtension>();
        for (var index = 0; index < FanOut; index++)
        {
            // The worker retains each canceled child's terminal state under the caller's child identity until the
            // caller's inbox accepts its completion, then deletes it.
            var childId = TaskId.CreateRoot("fan-out").Child($"wait-{index}");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (true)
            {
                try
                {
                    var childResponse = await worker.SubscribeOrPollAsync(
                        childId,
                        new SubscribeOrPollOptions { PollTimeout = TimeSpan.Zero },
                        cancellationToken);
                    Assert.Equal(DurableTaskStatus.Canceled, childResponse.Status);
                }
                catch (DurableTaskNotFoundException)
                {
                    break;
                }

                Assert.True(DateTime.UtcNow < deadline, $"Durable task '{childId}' was not deleted after its completion was delivered.");
                await Task.Delay(10, cancellationToken);
            }
        }
    }
}

/// <summary>Runs cross-grain messaging coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskMessagingTests() : DurableTaskMessagingTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs cross-grain messaging coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskMessagingTests() : DurableTaskMessagingTests(DurableTaskRecoveryFixture.OrleansBinary);

public interface IDurableTaskCoordinatorGrain : IGrainWithGuidKey
{
    DurableTask<int> SumEchoesAsync(Guid workerKey, int count);
    DurableTask<int> WaitForCancellationAsync(Guid workerKey, int count);
}

public interface IDurableTaskWorkerGrain : IGrainWithGuidKey
{
    DurableTask<int> EchoAsync(int value);
    DurableTask<int> WaitForCancellationAsync(int value);

    /// <summary>Gets the rejection codes of the worker's inbox dead letters.</summary>
    Task<string?[]> GetInboxDeadLetterCodesAsync();
}

public sealed class DurableTaskMessagingProbe
{
    private readonly ConcurrentDictionary<Guid, Counter> _started = [];
    private readonly ConcurrentDictionary<Guid, Counter> _canceled = [];

    public void RecordStarted(Guid workerKey) => _started.GetOrAdd(workerKey, static _ => new()).Increment();

    public void RecordCanceled(Guid workerKey) => _canceled.GetOrAdd(workerKey, static _ => new()).Increment();

    public int GetStartedCount(Guid workerKey) => _started.GetOrAdd(workerKey, static _ => new()).Count;

    public Task WaitForStartedAsync(Guid workerKey, int count) => _started.GetOrAdd(workerKey, static _ => new()).WaitForAsync(count);

    public Task WaitForCanceledAsync(Guid workerKey, int count) => _canceled.GetOrAdd(workerKey, static _ => new()).WaitForAsync(count);

    private sealed class Counter
    {
        private readonly List<(int Count, TaskCompletionSource Completion)> _waiters = [];
        private int _count;

        public int Count
        {
            get
            {
                lock (_waiters)
                {
                    return _count;
                }
            }
        }

        public void Increment()
        {
            lock (_waiters)
            {
                _count++;
                foreach (var waiter in _waiters.Where(waiter => waiter.Count <= _count).ToList())
                {
                    _waiters.Remove(waiter);
                    waiter.Completion.TrySetResult();
                }
            }
        }

        public Task WaitForAsync(int count)
        {
            lock (_waiters)
            {
                if (_count >= count)
                {
                    return Task.CompletedTask;
                }

                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, completion));
                return completion.Task;
            }
        }
    }
}

[GrainType("durable-task-coordinator-test")]
public sealed class DurableTaskCoordinatorGrain : DurableGrain, IDurableTaskCoordinatorGrain
{
    public async DurableTask<int> SumEchoesAsync(Guid workerKey, int count)
    {
        var worker = GrainFactory.GetGrain<IDurableTaskWorkerGrain>(workerKey);
        var scheduled = new List<ScheduledTask<int>>(count);
        for (var index = 0; index < count; index++)
        {
            scheduled.Add(await worker.EchoAsync(index).WithId($"echo-{index}").ScheduleAsync());
        }

        var sum = 0;
        foreach (var task in scheduled)
        {
            sum += await task.WaitAsync();
        }

        return sum;
    }

    public async DurableTask<int> WaitForCancellationAsync(Guid workerKey, int count)
    {
        var worker = GrainFactory.GetGrain<IDurableTaskWorkerGrain>(workerKey);
        var scheduled = new List<ScheduledTask<int>>(count);
        for (var index = 0; index < count; index++)
        {
            scheduled.Add(await worker.WaitForCancellationAsync(index).WithId($"wait-{index}").ScheduleAsync());
        }

        var sum = 0;
        foreach (var task in scheduled)
        {
            sum += await task.WaitAsync();
        }

        return sum;
    }
}

[GrainType("durable-task-worker-test")]
public sealed class DurableTaskWorkerGrain(DurableTaskMessagingProbe probe, IMessagingDiagnostics diagnostics)
    : DurableGrain, IDurableTaskWorkerGrain
{
    public Task<string?[]> GetInboxDeadLetterCodesAsync() =>
        Task.FromResult(diagnostics.InboxDeadLetters.Select(deadLetter => deadLetter.Code).ToArray());

    public DurableTask<int> EchoAsync(int value)
    {
        probe.RecordStarted(this.GetPrimaryKey());
        return DurableTask.FromResult(value * 2);
    }

    public DurableTask<int> WaitForCancellationAsync(int value) =>
        DurableTask.Run(async cancellationToken =>
        {
            probe.RecordStarted(this.GetPrimaryKey());
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                probe.RecordCanceled(this.GetPrimaryKey());
                throw;
            }

            return value;
        });
}

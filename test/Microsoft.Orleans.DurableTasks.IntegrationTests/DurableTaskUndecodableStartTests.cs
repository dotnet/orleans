using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.Journaling;
using Orleans.Messaging;
using Orleans.Messaging.Configuration;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>
/// Verifies that a start whose invocation its target can't decode is retried, and fails with
/// <see cref="DurableTaskDeliveryException"/> once <see cref="DurableMessagingOptions.OutboxRetryTimeout"/> elapses.
/// </summary>
/// <remarks>
/// The argument type models a request type which the target's deployment doesn't know: its deserialization always fails.
/// The outbox retries on the Durable Jobs clock, which the test advances.
/// </remarks>
public abstract class DurableTaskUndecodableStartTests : IAsyncLifetime
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryTimeout = TimeSpan.FromHours(4);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly UndecodableStartProbe _probe = new();
    private readonly DurableTaskRecoveryFixture _fixture;

    protected DurableTaskUndecodableStartTests(string journalFormatKey)
    {
        _fixture = new(journalFormatKey, silo =>
        {
            silo.Services.AddSingleton(_probe);
            silo.Services.AddSingleton<UndecodableArgumentHooks>();
            silo.Services.Configure<DurableMessagingOptions>(options =>
            {
                options.RetryInitialInterval = RetryDelay;
                options.OutboxRetryTimeout = RetryTimeout;
            });
        });
    }

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task UndecodableStartIsRetriedAndFailsWithDeliveryExceptionAfterTheRetryTimeout()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var starter = _fixture.Client.GetGrain<IUndecodableStartWorkflowGrain>(Guid.NewGuid());
        var target = _fixture.Client.GetGrain<IUndecodableStartTargetGrain>(Guid.NewGuid());
        using var failures = new FailedDeliveryListener(_fixture.Cluster.Silos
            .Select(silo => _fixture.Cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<OrleansInstruments>().Meter)
            .ToHashSet());

        var workflow = await starter.StartAsync(target.GetPrimaryKey()).WithId("workflow").ScheduleAsync(cancellationToken);
        var outcome = workflow.GetResponseAsync(cancellationToken);

        // The target throws on the invocation, so it dead-letters nothing and the starter's outbox schedules a retry,
        // which falls due only when the test advances the Durable Jobs clock.
        await failures.WaitForAsync(1).WaitAsync(Timeout, cancellationToken);
        Assert.Equal(1, _probe.DecodeAttempts);
        Assert.Empty(await target.GetInboxDeadLettersAsync());
        Assert.False(outcome.IsCompleted);

        _fixture.JobClock.Advance(RetryDelay * 2);
        await failures.WaitForAsync(2).WaitAsync(Timeout, cancellationToken);
        Assert.Equal(2, _probe.DecodeAttempts);
        Assert.Empty(await target.GetInboxDeadLettersAsync());
        Assert.False(outcome.IsCompleted);

        // Once the retry timeout elapses, the outbox dead-letters the invocation, and the start fails.
        _fixture.JobClock.Advance(RetryTimeout);
        var response = await outcome.WaitAsync(Timeout, cancellationToken);

        var childId = TaskId.CreateRoot("workflow").Child(UndecodableStartWorkflowGrain.StartId);
        var targetId = target.GetGrainId();
        var description = response.GetResult<string>();
        Assert.StartsWith(
            $"{childId} {targetId}|The invocation of durable task '{childId}' wasn't delivered to grain '{targetId}': ",
            description,
            StringComparison.Ordinal);
        Assert.Contains(UndecodableArgumentHooks.Error, description, StringComparison.Ordinal);

        var letter = Assert.Single(await starter.GetOutboxDeadLettersAsync());
        Assert.StartsWith("durable-rpc/invoke/", letter.Message.Envelope.MessageId.ToString(), StringComparison.Ordinal);
        Assert.True(letter.AttemptCount >= 2);
        Assert.Contains(UndecodableArgumentHooks.Error, letter.Reason, StringComparison.Ordinal);
        Assert.Empty(await target.GetInboxDeadLettersAsync());
    }
}

/// <summary>Runs undecodable start coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskUndecodableStartTests() : DurableTaskUndecodableStartTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs undecodable start coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskUndecodableStartTests() : DurableTaskUndecodableStartTests(DurableTaskRecoveryFixture.OrleansBinary);

/// <summary>An argument whose deserialization always fails, as for a type which the receiving deployment doesn't know.</summary>
[GenerateSerializer, Immutable, Alias("durable-task-undecodable-argument-test")]
[SerializationCallbacks(typeof(UndecodableArgumentHooks))]
public sealed class UndecodableArgument
{
    [Id(0)]
    public int Value { get; init; }
}

public sealed class UndecodableArgumentHooks(UndecodableStartProbe probe)
{
    public const string Error = "The argument's type is unknown to this deployment.";

    public void OnDeserialized(UndecodableArgument value)
    {
        probe.RecordDecodeAttempt();
        throw new InvalidOperationException(Error);
    }
}

public sealed class UndecodableStartProbe
{
    private int _decodeAttempts;

    public int DecodeAttempts => Volatile.Read(ref _decodeAttempts);

    public void RecordDecodeAttempt() => Interlocked.Increment(ref _decodeAttempts);
}

/// <summary>Counts the starter's failed outbox delivery attempts, which the outbox records once it schedules the retry.</summary>
internal sealed class FailedDeliveryListener : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<(int Count, TaskCompletionSource Completion)> _waiters = [];
    private int _failures;

    public FailedDeliveryListener(HashSet<Meter> meters)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (meters.Contains(instrument.Meter) && instrument.Name == "orleans-messaging-outbox-messages-delivered")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? grainType = null, status = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "grain_type") grainType = tag.Value as string;
                else if (tag.Key == "status") status = tag.Value as string;
            }

            if (grainType == UndecodableStartWorkflowGrain.GrainTypeName && status == "error")
            {
                OnFailure();
            }
        });
        _listener.Start();
    }

    public Task WaitForAsync(int count)
    {
        lock (_waiters)
        {
            if (_failures >= count)
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, completion));
            return completion.Task;
        }
    }

    public void Dispose() => _listener.Dispose();

    private void OnFailure()
    {
        lock (_waiters)
        {
            _failures++;
            foreach (var waiter in _waiters.Where(waiter => waiter.Count <= _failures).ToArray())
            {
                waiter.Completion.TrySetResult();
                _waiters.Remove(waiter);
            }
        }
    }
}

public interface IUndecodableStartWorkflowGrain : IGrainWithGuidKey
{
    DurableTask<string> StartAsync(Guid targetKey);
    Task<DeadLetter<OutboxMessage>[]> GetOutboxDeadLettersAsync();
}

public interface IUndecodableStartTargetGrain : IGrainWithGuidKey
{
    /// <remarks>
    /// The argument is declared as <see cref="object"/>, so only this test's silos, which register the argument's hooks,
    /// build the codec of <see cref="UndecodableArgument"/>; serializer validation builds the codecs of declared
    /// parameter types.
    /// </remarks>
    DurableTask<int> AcceptAsync(object argument);
    Task<DeadLetter<InboxMessage>[]> GetInboxDeadLettersAsync();
}

[GrainType(GrainTypeName)]
public sealed class UndecodableStartWorkflowGrain(IMessagingDiagnostics diagnostics) : DurableGrain, IUndecodableStartWorkflowGrain
{
    public const string GrainTypeName = "durable-task-undecodable-start-workflow-test";
    public const string StartId = "start";

    public async DurableTask<string> StartAsync(Guid targetKey)
    {
        var target = GrainFactory.GetGrain<IUndecodableStartTargetGrain>(targetKey);
        try
        {
            await target.AcceptAsync(new UndecodableArgument { Value = 1 }).WithId(StartId).ScheduleAsync();
            return "accepted";
        }
        catch (DurableTaskDeliveryException undelivered)
        {
            return $"{undelivered.TaskId} {undelivered.Target}|{undelivered.Message}";
        }
    }

    public Task<DeadLetter<OutboxMessage>[]> GetOutboxDeadLettersAsync() => Task.FromResult(diagnostics.OutboxDeadLetters.ToArray());
}

[GrainType("durable-task-undecodable-start-target-test")]
public sealed class UndecodableStartTargetGrain(IMessagingDiagnostics diagnostics) : DurableGrain, IUndecodableStartTargetGrain
{
    public DurableTask<int> AcceptAsync(object argument) => DurableTask.FromResult(((UndecodableArgument)argument).Value);

    public Task<DeadLetter<InboxMessage>[]> GetInboxDeadLettersAsync() => Task.FromResult(diagnostics.InboxDeadLetters.ToArray());
}

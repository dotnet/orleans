using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Messaging;
using Orleans.Messaging.Configuration;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>
/// Verifies that a completion whose stored result no longer decodes is dead-lettered without disrupting its grain.
/// </summary>
/// <remarks>
/// The result type models one whose deserialization fails after a later deployment. The callee holds the decoded result
/// while the activation which completed the task lives, so the test keeps the completion pending, by making its caller
/// unavailable, until a new callee activation must decode the stored result to deliver it.
/// </remarks>
public abstract class DurableTaskCompletionDeadLetterTests : IAsyncLifetime
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);
    private readonly FragileResultProbe _probe = new();
    private readonly DurableTaskRecoveryFixture _fixture;

    protected DurableTaskCompletionDeadLetterTests(string journalFormatKey)
    {
        _fixture = new(journalFormatKey, silo =>
        {
            silo.Services.AddSingleton(_probe);
            silo.Services.AddSingleton<FragileResultHooks>();

            // One delivery attempt fails in the first callee activation; the retry falls due only when the test
            // advances the Durable Jobs clock.
            silo.Services.Configure<DurableMessagingOptions>(options => options.BackpressureRetryDelay = RetryDelay);
        });
    }

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task UndecodableCompletionResultIsDeadLetteredAndTheCalleeKeepsWorking()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var caller = _fixture.Client.GetGrain<IFragileResultCallerGrain>(Guid.NewGuid());
        var callee = _fixture.Client.GetGrain<IFragileResultCalleeGrain>(Guid.NewGuid());
        var probe = _probe;
        using var deliveries = new DeliveryOutcomeListener(_fixture.Cluster.Silos
            .Select(silo => _fixture.Cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<OrleansInstruments>().Meter)
            .ToHashSet());

        await caller.CallAsync(callee.GetPrimaryKey()).ScheduleAsync("fragile-root", cancellationToken);
        await probe.ProduceStarted.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        // The caller becomes unavailable, so the callee's first activation can't deliver the completion.
        probe.BlockCallerActivation = true;
        await caller.RequestDeactivationAsync();
        await probe.CallerDeactivated.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        var attemptFailed = deliveries.WaitForAsync("error");
        probe.ReleaseProduce();
        await attemptFailed.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Empty(await callee.GetOutboxDeadLettersAsync());

        // A new callee activation recovers the completion and must decode its stored result to deliver it.
        await callee.RequestDeactivationAsync();
        await probe.CalleeDeactivated.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        _fixture.JobClock.Advance(RetryDelay * 2);
        var deadLettered = deliveries.WaitForAsync("source_error");
        Assert.Empty(await callee.GetOutboxDeadLettersAsync());
        await deadLettered.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        var letter = Assert.Single(await callee.GetOutboxDeadLettersAsync());
        Assert.StartsWith("durable-rpc/complete/", letter.Message.Envelope.MessageId.ToString(), StringComparison.Ordinal);
        Assert.True(letter.Message.ReceiverId.IsDefault);
        Assert.Empty(letter.Message.Envelope.Payload);
        Assert.StartsWith("The outbox message source failed to provide the message: ", letter.Reason, StringComparison.Ordinal);
        Assert.Contains(FragileResultHooks.Error, letter.Reason, StringComparison.Ordinal);
        Assert.Equal(2, letter.AttemptCount);

        // The callee keeps its activation and processes later work.
        var later = await callee.DoubleAsync(21).ScheduleAsync("later", cancellationToken);
        var response = await later.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(42, response.GetResult<int>());
        Assert.Equal(2, probe.CalleeActivations);
        Assert.Single(await callee.GetOutboxDeadLettersAsync());

        // The dead-lettered completion retains its result for polling, and polling surfaces the decoding failure.
        var childId = TaskId.CreateRoot("fragile-root").Child(FragileResultCallerGrain.CallId);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => callee.AsReference<IDurableTaskGrainExtension>()
            .SubscribeOrPollAsync(childId, new SubscribeOrPollOptions { PollTimeout = TimeSpan.Zero }, cancellationToken).AsTask());
        Assert.Contains(FragileResultHooks.Error, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, probe.CalleeActivations);

        // Diagnostics report the undecodable result and keep listing the other tasks.
        var diagnostics = new Dictionary<TaskId, DurableTaskDiagnosticState>();
        await foreach (var (taskId, state) in callee.AsReference<IDurableTaskGrainExtension>().GetTasksAsync(cancellationToken))
        {
            diagnostics[taskId] = state;
        }

        Assert.Equal("Unreadable", diagnostics[childId].Status);
        Assert.Contains(FragileResultHooks.Error, diagnostics[childId].Response, StringComparison.Ordinal);
        Assert.Equal("Completed", diagnostics[TaskId.CreateRoot("later")].Status);
        Assert.Equal("42", diagnostics[TaskId.CreateRoot("later")].Response);
    }

    /// <summary>Observes outbox delivery outcomes recorded by the cluster's silos.</summary>
    private sealed class DeliveryOutcomeListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _outcomes = new();

        public DeliveryOutcomeListener(HashSet<Meter> meters)
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

                if (grainType == FragileResultCalleeGrain.GrainTypeName && status is not null)
                {
                    Get(status).TrySetResult();
                }
            });
            _listener.Start();
        }

        public Task WaitForAsync(string status) => Get(status).Task;

        private TaskCompletionSource Get(string status) =>
            _outcomes.GetOrAdd(status, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        public void Dispose() => _listener.Dispose();
    }
}

/// <summary>Runs completion dead-lettering coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskCompletionDeadLetterTests() : DurableTaskCompletionDeadLetterTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs completion dead-lettering coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskCompletionDeadLetterTests() : DurableTaskCompletionDeadLetterTests(DurableTaskRecoveryFixture.OrleansBinary);

/// <summary>A result whose stored form can no longer be decoded, as after its type changes incompatibly.</summary>
[GenerateSerializer, Immutable, Alias("durable-task-fragile-result-test")]
[SerializationCallbacks(typeof(FragileResultHooks))]
public sealed class FragileResult
{
    [Id(0)]
    public required string Value { get; init; }
}

public sealed class FragileResultHooks
{
    public const string Error = "The fragile result can no longer be decoded.";

    public void OnDeserialized(FragileResult value) => throw new InvalidOperationException(Error);
}

public sealed class FragileResultProbe
{
    private readonly TaskCompletionSource _produceStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _produce = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _callerDeactivated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _calleeDeactivated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calleeActivations;

    public volatile bool BlockCallerActivation;

    public Task ProduceStarted => _produceStarted.Task;
    public Task CallerDeactivated => _callerDeactivated.Task;
    public Task CalleeDeactivated => _calleeDeactivated.Task;
    public int CalleeActivations => Volatile.Read(ref _calleeActivations);

    public Task StartProduceAsync(CancellationToken cancellationToken)
    {
        _produceStarted.TrySetResult();
        return _produce.Task.WaitAsync(cancellationToken);
    }

    public void ReleaseProduce() => _produce.TrySetResult();
    public void OnCallerDeactivated() => _callerDeactivated.TrySetResult();
    public void OnCalleeActivated() => Interlocked.Increment(ref _calleeActivations);
    public void OnCalleeDeactivated() => _calleeDeactivated.TrySetResult();
}

public interface IFragileResultCallerGrain : IGrainWithGuidKey
{
    DurableTask<string> CallAsync(Guid calleeKey);
    Task RequestDeactivationAsync();
}

public interface IFragileResultCalleeGrain : IGrainWithGuidKey
{
    DurableTask<FragileResult> ProduceAsync();
    DurableTask<int> DoubleAsync(int value);
    Task<DeadLetter<OutboxMessage>[]> GetOutboxDeadLettersAsync();
    Task RequestDeactivationAsync();
}

[GrainType("durable-task-fragile-caller-test")]
public sealed class FragileResultCallerGrain(FragileResultProbe probe) : DurableGrain, IFragileResultCallerGrain
{
    public const string CallId = "produce";

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (probe.BlockCallerActivation)
        {
            throw new InvalidOperationException("The caller is unavailable.");
        }

        return base.OnActivateAsync(cancellationToken);
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        probe.OnCallerDeactivated();
        return base.OnDeactivateAsync(reason, cancellationToken);
    }

    public async DurableTask<string> CallAsync(Guid calleeKey)
    {
        var result = await GrainFactory.GetGrain<IFragileResultCalleeGrain>(calleeKey).ProduceAsync().WithId(CallId);
        return result.Value;
    }

    public Task RequestDeactivationAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }
}

[GrainType(GrainTypeName)]
public sealed class FragileResultCalleeGrain(FragileResultProbe probe, IMessagingDiagnostics diagnostics)
    : DurableGrain, IFragileResultCalleeGrain
{
    public const string GrainTypeName = "durable-task-fragile-callee-test";

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        probe.OnCalleeActivated();
        return base.OnActivateAsync(cancellationToken);
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        probe.OnCalleeDeactivated();
        return base.OnDeactivateAsync(reason, cancellationToken);
    }

    public DurableTask<FragileResult> ProduceAsync() =>
        DurableTask.Run(async cancellationToken =>
        {
            await probe.StartProduceAsync(cancellationToken);
            return new FragileResult { Value = "fragile" };
        });

    public DurableTask<int> DoubleAsync(int value) => DurableTask.FromResult(value * 2);

    public Task<DeadLetter<OutboxMessage>[]> GetOutboxDeadLettersAsync() => Task.FromResult(diagnostics.OutboxDeadLetters.ToArray());

    public Task RequestDeactivationAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }
}

using System.Collections.Concurrent;
using DurableOrderProcessing;
using DurableOrderProcessing.Grains;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Orleans.Messaging;
using Orleans.Messaging.Configuration;
using Orleans.Messaging.Diagnostics;
using Orleans.TestingHost.Diagnostics;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>Exercises reservation workflows against real task, messaging, journal, and scheduling services.</summary>
public abstract class DurableTaskApplicationScenarioTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);
    private const int Units = 2;
    private const string RootId = "reserve-order";
    private readonly ReservationDeliveryControl _deliveries = new();
    private readonly ScenarioRetryCommitProbe _commits = new();
    private readonly DurableTaskRecoveryFixture _fixture;
    private readonly string _journalFormatKey;
    private string _lastBarrier = "cluster initialization";
    private string _schedule = "no retry observed";

    protected DurableTaskApplicationScenarioTests(string journalFormatKey)
    {
        _journalFormatKey = journalFormatKey;
        _fixture = new(journalFormatKey, silo =>
        {
            silo.Services.Configure<JsonJournalOptions>(options =>
                options.AddTypeInfoResolver(DurableOrderProcessingJsonContext.Default));
            silo.AddOutgoingGrainCallFilter(_deliveries);
            silo.Services.AddSingleton(_commits);
            silo.Services.Configure<DurableMessagingOptions>(options => options.RetryInitialInterval = RetryDelay);
        });
    }

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _fixture.DisposeAsync();
        }
        finally
        {
            _commits.Dispose();
        }
    }

    [Fact]
    public async Task CommittedReservationIntentRecoversBusinessResultAfterSenderDeactivation()
    {
        // Contract: a committed outgoing reservation survives graceful sender deactivation without the client resending it.
        var (order, catalog, initial, task, retry) = await StartInterruptedReservationAsync(loseAcceptedReply: false);
        Assert.True(_fixture.Cluster.TryGetGrainContext(order.GetGrainId(), out var before));

        await _fixture.Cluster.DeactivateAsync(order);
        await AssertRetryNotDueAsync(order, task, retry);
        Assert.True(_fixture.Cluster.TryGetGrainContext(order.GetGrainId(), out var after));
        Assert.NotSame(before, after);
        Assert.Equal(initial, await catalog.GetAsync());

        AdvanceToRetry(retry);
        await AssertReservedAsync(order, catalog, initial, task);
    }

    [Fact]
    public async Task LostAcceptanceReplyRedeliversSameReservationAfterReceiverRecovery()
    {
        // Contract: losing an accepted answer and recovering the receiver does not reserve the stock twice.
        var (order, catalog, initial, task, retry) = await StartInterruptedReservationAsync(loseAcceptedReply: true);
        // Acceptance commits inbox admission, not necessarily execution. Wait for the detached application's own result.
        Assert.True((await WaitAsync(
            catalog.GetDurableTask<bool>("catalog-reservation").GetResponseAsync(Token), order, "receiver business result")).GetResult<bool>());
        await AssertCommittedStockAsync(catalog, Reserved(initial));
        Assert.True(_fixture.Cluster.TryGetGrainContext(catalog.GetGrainId(), out var oldReceiver));
        await _fixture.Cluster.DeactivateAsync(catalog);
        Assert.Equal(Reserved(initial), await catalog.GetAsync());
        Assert.True(_fixture.Cluster.TryGetGrainContext(catalog.GetGrainId(), out var newReceiver));
        Assert.NotSame(oldReceiver, newReceiver);
        Assert.True(_fixture.JobClock.GetUtcNow() < retry.NextAttemptAt);
        Assert.Single(_deliveries.Attempts);

        AdvanceToRetry(retry);
        await AssertReservedAsync(order, catalog, initial, task);
        Assert.Equal([DeliveryStatus.Accepted, DeliveryStatus.Duplicate], _deliveries.Answers);
    }

    [Fact]
    public async Task CommittedReservationIntentRecoversBusinessResultAfterImmediateSiloLoss()
    {
        // This is the testing host's immediate silo-loss boundary, not a process kill or a storage-device crash.
        var (order, catalog, initial, task, retry) = await StartInterruptedReservationAsync(loseAcceptedReply: false);
        Assert.True(_fixture.Cluster.TryGetGrainContext(order.GetGrainId(), out var activation));
        var originalSilo = activation.Address.SiloAddress;
        var silo = _fixture.Cluster.Silos.Single(candidate => candidate.SiloAddress.Equals(originalSilo));

        await WaitAsync(_fixture.Cluster.KillSiloAsync(silo, Token), order, "immediate silo loss");
        await WaitAsync(_fixture.Cluster.WaitForLivenessToStabilizeAsync(didKill: true), order, "membership convergence");
        await AssertRetryNotDueAsync(order, task, retry);
        Assert.True(_fixture.Cluster.TryGetGrainContext(order.GetGrainId(), out var recovered));
        Assert.NotEqual(originalSilo, recovered.Address.SiloAddress);

        AdvanceToRetry(retry);
        await AssertReservedAsync(order, catalog, initial, task);
    }

    [Fact]
    public async Task CancellationAfterOneParallelReservationCompensatesOnlyTheSuccessfulLine()
    {
        // Contract: cancellation releases the completed parallel line, not the line still awaiting validation.
        var order = _fixture.Client.GetGrain<IReservationScenarioGrain>(Guid.NewGuid());
        var first = _fixture.Client.GetGrain<ICatalogItemGrain>(1);
        var second = _fixture.Client.GetGrain<ICatalogItemGrain>(2);
        var initialFirst = Assert.IsType<CatalogItem>(await first.GetAsync());
        var initialSecond = Assert.IsType<CatalogItem>(await second.GetAsync());
        var observedSuccess = _fixture.MessagingProbe.WaitForStartedAsync(order.GetPrimaryKey(), 1);
        var task = await order.ReserveParallelAsync(Guid.NewGuid(), Units).WithId("parallel-order").ScheduleAsync(Token);
        await WaitAsync(observedSuccess, order, "one parallel reservation completed; other line awaiting validation");
        Assert.Equal(Reserved(initialFirst), await first.GetAsync());
        Assert.Equal(initialSecond, await second.GetAsync());

        await task.CancelAsync(Token);
        var response = await WaitAsync(task.GetResponseAsync(Token), order, "compensation scheduled");

        Assert.Equal("compensated:1", response.GetResult<string>());
        Assert.True((await WaitAsync(
            first.GetDurableTask<bool>(ReservationScenarioGrain.ReleaseId(order.GetPrimaryKey(), 1)).GetResponseAsync(Token),
            order, "successful line release result")).GetResult<bool>());
        await Assert.ThrowsAsync<DurableTaskNotFoundException>(() =>
            second.GetDurableTask<bool>(ReservationScenarioGrain.ReleaseId(order.GetPrimaryKey(), 2))
                .GetResponseAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, Token));
        Assert.Equal(initialFirst, await first.GetAsync());
        Assert.Equal(initialSecond, await second.GetAsync());
        await AssertCommittedStockAsync(first, initialFirst);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<(IReservationScenarioGrain Order, ICatalogItemGrain Catalog, CatalogItem Initial,
        ScheduledTask<bool> Task, MessagingEvents.OutboxRetryScheduled Retry)> StartInterruptedReservationAsync(bool loseAcceptedReply)
    {
        var order = _fixture.Client.GetGrain<IReservationScenarioGrain>(Guid.NewGuid());
        var catalog = _fixture.Client.GetGrain<ICatalogItemGrain>(1);
        var initial = Assert.IsType<CatalogItem>(await catalog.GetAsync());
        _deliveries.Arm(catalog.GetGrainId(), loseAcceptedReply);
        using var events = new DiagnosticEventCollector(MessagingEvents.ListenerName);
        var task = await order.ReserveAsync(Guid.NewGuid(), Units).WithId(RootId).ScheduleAsync(Token);
        var retry = (MessagingEvents.OutboxRetryScheduled)(await WaitAsync(events.WaitForEventAsync(
            nameof(MessagingEvents.OutboxRetryScheduled),
            evt => evt.Payload is MessagingEvents.OutboxRetryScheduled payload
                && payload.GrainContext.GrainId == order.GetGrainId()
                && payload.ReceiverId == catalog.GetGrainId(),
            Timeout, Token), order, "first reservation delivery failed; retry staged")).Payload!;
        _schedule = $"{retry.MessageId}, attempt {retry.AttemptCount}, due {retry.NextAttemptAt:O}";

        // The event describes staging. The registered state's callback observes acknowledgement of a capture
        // which includes that staging, rather than acknowledgement of an earlier, concurrent write.
        await WaitAsync(_commits.WaitAsync(order.GetGrainId()), order, "retry and outgoing intent acknowledged by storage");
        Assert.Equal(1, retry.AttemptCount);
        Assert.Equal(Assert.Single(_deliveries.Attempts).Envelope.MessageId, retry.MessageId);
        return (order, catalog, initial, task, retry);
    }

    private async Task AssertRetryNotDueAsync(
        IReservationScenarioGrain order, ScheduledTask<bool> task, MessagingEvents.OutboxRetryScheduled retry)
    {
        // Polling is an interleavable runtime request, so it can also activate a recovering sender whose application
        // segment is waiting for remote acceptance. An ordinary grain call would wait behind that segment.
        Assert.False(await WaitAsync(
            task.IsCompletedAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, Token), order, "retry not due"));
        Assert.True(_fixture.JobClock.GetUtcNow() < retry.NextAttemptAt);
        Assert.Single(_deliveries.Attempts);
    }

    private void AdvanceToRetry(MessagingEvents.OutboxRetryScheduled retry)
    {
        _lastBarrier = $"advancing Durable Jobs clock to retry {retry.MessageId} at {retry.NextAttemptAt:O}";
        _fixture.JobClock.Advance(retry.NextAttemptAt - _fixture.JobClock.GetUtcNow() + TimeSpan.FromSeconds(1));
    }

    private async Task AssertReservedAsync(
        IReservationScenarioGrain order, ICatalogItemGrain catalog, CatalogItem initial, ScheduledTask<bool> task)
    {
        await WaitAsync(_deliveries.Redelivered, order, "reservation redelivered and receiver answered");
        Assert.True((await WaitAsync(task.GetResponseAsync(Token), order, "order business result")).GetResult<bool>());
        Assert.Equal(Reserved(initial), await catalog.GetAsync());
        await AssertCommittedStockAsync(catalog, Reserved(initial));
        var attempts = _deliveries.Attempts;
        Assert.Equal(2, attempts.Count);
        Assert.Equal(attempts[0].Envelope.MessageId, attempts[1].Envelope.MessageId);
        Assert.Equal(attempts[0].SentAt, attempts[1].SentAt);
        Assert.Equal(attempts[0].Envelope.Subject, attempts[1].Envelope.Subject);
    }

    private static CatalogItem Reserved(CatalogItem initial) =>
        initial with { AvailableStock = initial.AvailableStock - Units, ReservedStock = initial.ReservedStock + Units };

    private async Task AssertCommittedStockAsync(ICatalogItemGrain catalog, CatalogItem expected)
    {
        // Replay serialized storage into a new state machine using the production format codec. Never read activation state.
        var services = _fixture.Cluster.GetSiloServiceProvider();
        await using var reader = services.GetRequiredService<IJournaledStateManagerFactory>()
            .CreateStandalone(JournalId.FromGrainId(catalog.GetGrainId()));
        var stock = new CommittedCatalogReader(
            services.GetRequiredKeyedService<IDurableValueCommandCodec<CatalogItem>>(_journalFormatKey));
        reader.RegisterStateMachine("item", stock);
        await reader.InitializeAsync(Token);
        Assert.Equal(expected, stock.Value);
    }

    private async Task<T> WaitAsync<T>(Task<T> pending, IReservationScenarioGrain order, string phase)
    {
        try
        {
            var result = await pending.WaitAsync(Timeout, Token);
            _lastBarrier = phase;
            return result;
        }
        catch (TimeoutException exception)
        {
            var active = _fixture.Cluster.TryGetGrainContext(order.GetGrainId(), out var context);
            throw new TimeoutException(
                $"Phase: {phase}; order: {order.GetGrainId()}; last barrier: {_lastBarrier}; "
                + $"pending: {pending.Status}; active: {active}; silo: {context?.Address.SiloAddress}; "
                + $"delivery attempts: {_deliveries.Attempts.Count}; retry schedule: {_schedule}; "
                + $"redelivery wait: {_deliveries.Redelivered.Status}; job clock: {_fixture.JobClock.GetUtcNow():O}.", exception);
        }
    }

    private async Task WaitAsync(Task pending, IReservationScenarioGrain order, string phase) =>
        await WaitAsync(CompleteAsync(pending), order, phase);

    private static async Task<bool> CompleteAsync(Task pending)
    {
        await pending;
        return true;
    }

    /// <summary>A read-only projection of committed catalog bytes; it never journals or owns live application state.</summary>
    private sealed class CommittedCatalogReader(IDurableValueCommandCodec<CatalogItem> codec)
        : IStateMachine, IDurableValueCommandHandler<CatalogItem>
    {
        public CatalogItem? Value { get; private set; }
        public void ApplySet(CatalogItem value) => Value = value;
        public void Reset(JournalStreamWriter writer) => Value = null;
        public void ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
            context.GetRequiredCommandCodec(entry.FormatKey, codec).Apply(entry.Reader, this);
        public void WritePendingEntries(JournalStreamWriter writer) { }
        public void WriteSnapshot(JournalStreamWriter writer) => throw new InvalidOperationException("Read-only recovery projection.");
    }

    /// <summary>Fails one selected delivery before admission or after durable acceptance; records every retry's identity.</summary>
    private sealed class ReservationDeliveryControl : IOutgoingGrainCallFilter
    {
        private readonly List<InboxMessage> _attempts = [];
        private readonly List<DeliveryStatus> _answers = [];
        private GrainId _target;
        private bool _loseAcceptedReply;
        private readonly TaskCompletionSource _redelivered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<InboxMessage> Attempts { get { lock (_attempts) return [.. _attempts]; } }
        public IReadOnlyList<DeliveryStatus> Answers { get { lock (_attempts) return [.. _answers]; } }
        public Task Redelivered => _redelivered.Task;

        public void Arm(GrainId target, bool loseAcceptedReply)
        {
            _target = target;
            _loseAcceptedReply = loseAcceptedReply;
        }

        public async Task Invoke(IOutgoingGrainCallContext context)
        {
            if (context.TargetId != _target || context.InterfaceMethod?.DeclaringType != typeof(IMessagingExtension)
                || context.Request.GetArgument(0) is not InboxMessage message)
            {
                await context.Invoke();
                return;
            }

            bool first;
            lock (_attempts)
            {
                first = _attempts.Count == 0;
                _attempts.Add(message);
            }

            if (first && !_loseAcceptedReply)
            {
                throw new TimeoutException("Reservation delivery lost before receiver admission.");
            }

            await context.Invoke();
            var answer = Assert.IsType<DeliveryResult>(context.Result);
            lock (_attempts) _answers.Add(answer.Status);
            if (first && _loseAcceptedReply)
            {
                Assert.Equal(DeliveryStatus.Accepted, answer.Status);
                throw new TimeoutException("Reservation accepted; its answer was lost.");
            }

            _redelivered.TrySetResult();
        }
    }
}

[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT"), TestProvider("None"), TestArea("DurableTasks")]
public sealed class JsonDurableTaskApplicationScenarioTests()
    : DurableTaskApplicationScenarioTests(DurableTaskRecoveryFixture.Json);

[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT"), TestProvider("None"), TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskApplicationScenarioTests()
    : DurableTaskApplicationScenarioTests(DurableTaskRecoveryFixture.OrleansBinary);

public interface IReservationScenarioGrain : IGrainWithGuidKey
{
    DurableTask<bool> ReserveAsync(Guid orderId, int units);
    DurableTask<string> ReserveParallelAsync(Guid orderId, int units);
}

[GrainType("durable-reservation-scenario")]
public sealed class ReservationScenarioGrain : DurableGrain, IReservationScenarioGrain
{
    private readonly DurableTaskMessagingProbe _probe;

    public ReservationScenarioGrain(
        IJournaledStateManager manager, ScenarioRetryCommitProbe commits, DurableTaskMessagingProbe probe)
    {
        manager.RegisterStateMachine("scenario-retry-commit", commits.CreateWitness(this.GetGrainId()));
        _probe = probe;
    }

    public async DurableTask<bool> ReserveAsync(Guid orderId, int units)
    {
        var reservation = await GrainFactory.GetGrain<ICatalogItemGrain>(1).ReserveAsync(orderId, units)
            .Detached("catalog-reservation").ScheduleAsync();
        return await reservation.WaitAsync();
    }

    public static string ReleaseId(Guid orderKey, int product) => $"release-{orderKey:N}-{product}";

    public async DurableTask<string> ReserveParallelAsync(Guid orderId, int units)
    {
        var confirmed = new List<int>();
        try
        {
            await foreach (var result in DurableTask.WhenEach([
                ReserveLineAsync(orderId, 1, units, awaitValidation: false),
                ReserveLineAsync(orderId, 2, units, awaitValidation: true)]))
            {
                var product = result.GetResult();
                if (product != 0)
                {
                    confirmed.Add(product);
                    _probe.RecordStarted(this.GetPrimaryKey());
                }
            }

            return "completed";
        }
        catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
        {
            foreach (var product in confirmed)
            {
                await GrainFactory.GetGrain<ICatalogItemGrain>(product).ReleaseAsync(orderId)
                    .Detached(ReleaseId(this.GetPrimaryKey(), product)).ScheduleAsync();
            }

            return $"compensated:{confirmed.Count}";
        }
    }

    private async DurableTask<int> ReserveLineAsync(Guid orderId, int product, int units, bool awaitValidation)
    {
        if (awaitValidation)
        {
            await new DurableSignal<bool>("line-validation").WaitAsync();
        }

        return await GrainFactory.GetGrain<ICatalogItemGrain>(product).ReserveAsync(orderId, units).WithId("reserve")
            ? product : 0;
    }
}

/// <summary>Pairs the synchronous retry-staging hook with its owning journal's acknowledged capture.</summary>
public sealed class ScenarioRetryCommitProbe : IObserver<MessagingEvents.MessagingEvent>, IDisposable
{
    private readonly ConcurrentDictionary<GrainId, Retry> _retries = new();
    private readonly IDisposable _subscription;

    public ScenarioRetryCommitProbe() => _subscription = MessagingEvents.AllEvents.Subscribe(this);
    public void Dispose() => _subscription.Dispose();
    public Task WaitAsync(GrainId grainId) => _retries[grainId].Committed.Task;
    public IStateMachine CreateWitness(GrainId grainId) => new Witness(_retries.GetOrAdd(grainId, static _ => new()));
    public void OnCompleted() { }
    public void OnError(Exception error) => throw new InvalidOperationException("The synchronous diagnostic stream failed.", error);
    public void OnNext(MessagingEvents.MessagingEvent value)
    {
        if (value is MessagingEvents.OutboxRetryScheduled && _retries.TryGetValue(value.GrainContext.GrainId, out var retry))
        {
            retry.Staged = true;
        }
    }

    private sealed class Retry
    {
        public volatile bool Staged;
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Witness(Retry retry) : IStateMachine
    {
        private bool _captured;
        public void ReplayEntry(JournalEntry entry, JournalReplayContext context) => throw new InvalidOperationException("No entries.");
        public void Reset(JournalStreamWriter writer) => _captured = false;
        public void WritePendingEntries(JournalStreamWriter writer) => _captured = retry.Staged;
        public void WriteSnapshot(JournalStreamWriter writer) => _captured = retry.Staged;
        public void OnWriteCompleted()
        {
            if (_captured) retry.Committed.TrySetResult();
        }
    }
}

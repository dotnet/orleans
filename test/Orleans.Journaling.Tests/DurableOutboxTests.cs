using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.DurableJobs;
using Orleans.DurableMessaging;
using Orleans.DurableMessaging.Configuration;
using Orleans.Journaling;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Focused unit tests for <see cref="DurableOutbox"/>, targeting the previously-uncovered branches of
/// <c>DeliverPendingMessagesAsync</c> (backpressure, route-not-found, unexpected status, and delivery
/// exceptions), <c>RecordDeliveryFailure</c> (dead-lettering by attempt count vs. by message age, and
/// exponential retry backoff), and <c>ExecuteJobAsync</c> (job-id ownership, claiming, and release).
/// </summary>
[TestCategory("BVT"), TestCategory("Journaling")]
public class DurableOutboxTests : JournalingTestBase
{
    private static readonly GrainId SenderId = GrainId.Create("sender", "s1");
    private static readonly GrainId ReceiverId = GrainId.Create("receiver", "r1");
    private const string RouteKey = "test.route";

    private DurableEnvelope CreateEnvelope()
    {
        var builder = new DurableEnvelopeBuilder(SessionPool, SenderId);
        return builder.To(ReceiverId, RouteKey).WithBody("payload").Build();
    }

    private sealed class Harness
    {
        public required DurableOutbox Outbox { get; init; }
        public required TestInboxExtension Extension { get; init; }
        public required TestDurableValue<string> JobId { get; init; }
        public required TestDurableDictionary<Guid, OutboxMessageState> MessageStates { get; init; }
        public required TestDurableDictionary<Guid, OutboxDeadLetter> DeadLetters { get; init; }
        public required FakeTimeProvider TimeProvider { get; init; }
        public required IJournaledStateManager StateManager { get; init; }
    }

    /// <summary>
    /// Constructs a <see cref="DurableOutbox"/> with hand-written fakes for every collaborator except the
    /// real <see cref="JournaledStateManagerShared"/> (needed to resolve the base <see cref="DurableDictionary{K,V}"/>
    /// command codec via DI) and a no-op <see cref="IJournaledStateManager"/> (so RegisterState/WriteStateAsync
    /// don't require a full journal). By default the job id is pre-seeded with a sentinel value so the
    /// fire-and-forget background job-scheduling path (triggered from OnWriteCompleted) never actually runs,
    /// keeping delivery-focused tests fully deterministic.
    /// </summary>
    private Harness CreateOutbox(
        int maxDeliveryAttempts = 100,
        TimeSpan? maxRetryAge = null,
        TimeSpan? backpressureRetryDelay = null,
        int batchSize = 32,
        string? initialJobId = "unused-job-id-sentinel")
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);

        // Build a real JournaledStateManager (backed by in-memory storage) so that the base
        // DurableDictionary<Guid, DurableEnvelope> ctor can resolve a real command codec via DI, and so
        // that Send()/OnWriteCompleted() exercise the actual journal-writer initialization path instead of
        // hitting the Debug.Assert(_writer.IsInitialized) guard that a no-op fake manager would trip.
        var shared = new JournaledStateManagerShared(
            ServiceProvider.GetRequiredService<ILogger<JournaledStateManager>>(),
            Options.Create(ManagerOptions),
            timeProvider,
            ServiceProvider);
        var storage = new VolatileJournalStorage();
        storage.SetConfiguredJournalFormatKey(ManagerOptions.JournalFormatKey);
        var manager = new JournaledStateManager(shared, storage);
        var lifecycle = new TestGrainLifecycle(ServiceProvider.GetRequiredService<ILogger<TestGrainLifecycle>>());
        (manager as ILifecycleParticipant<IGrainLifecycle>)?.Participate(lifecycle);

        var extension = new TestInboxExtension();
        var grainFactory = new TestGrainFactory(extension);
        var grainContext = new MockGrainContext();
        var jobId = new TestDurableValue<string> { Value = initialJobId };
        var messageStates = new TestDurableDictionary<Guid, OutboxMessageState>();
        var deadLetters = new TestDurableDictionary<Guid, OutboxDeadLetter>();
        var jobHandlers = new TestJobHandlerRegistry();
        var jobManager = new TestJobManager();

        var outbox = new DurableOutbox(
            "outbox",
            manager,
            shared,
            ServiceProvider,
            grainFactory,
            grainContext,
            NullLogger<DurableOutbox>.Instance,
            DurableMessagingInstruments.CreateForDirectConstruction(),
            messageStates,
            deadLetters,
            jobId,
            jobManager,
            jobHandlers,
            timeProvider,
            Options.Create(new DurableInboxOptions
            {
                BackpressureRetryDelay = backpressureRetryDelay ?? TimeSpan.FromSeconds(1),
                MaxOutboxRetryAge = maxRetryAge ?? TimeSpan.FromDays(1),
                MaxDeliveryAttempts = maxDeliveryAttempts,
                OutboxBatchSize = batchSize
            }));
        grainFactory.OutboxCommitExtension = outbox;

        // Drive the lifecycle's Activate stage so the manager processes its queued RegisterState work item
        // and initializes the outbox's JournalStreamWriter (mirrors what a real grain activation does).
        lifecycle.OnStart(CancellationToken.None).GetAwaiter().GetResult();

        return new Harness
        {
            Outbox = outbox,
            Extension = extension,
            JobId = jobId,
            MessageStates = messageStates,
            DeadLetters = deadLetters,
            TimeProvider = timeProvider,
            StateManager = manager
        };
    }

    /// <summary>
    /// Sends the envelope and marks it durable (simulating a completed WriteStateAsync) without invoking the
    /// real background job-scheduling pump, so tests remain synchronous and deterministic.
    /// </summary>
    private static void SendAndMarkDurable(DurableOutbox outbox, DurableEnvelope envelope)
    {
        outbox.Send(envelope);
        ((IJournaledState)outbox).OnWriteCompleted();
    }

    [Fact]
    public async Task DeliverPendingMessagesAsync_WhenReceiverReportsBackpressure_KeepsMessageAndSchedulesRetry()
    {
        var h = CreateOutbox();
        var envelope = CreateEnvelope();
        SendAndMarkDurable(h.Outbox, envelope);
        h.Extension.NextResult = DeliveryResult.Backpressured();

        await h.Outbox.DeliverPendingMessagesAsync();

        // Message must remain in the outbox (not delivered), with exactly one attempt recorded.
        Assert.True(h.Outbox.TryGetMessage(envelope.MessageId, out _));
        Assert.Single(h.Extension.DeliveredEnvelopes);
        Assert.True(h.MessageStates.TryGetValue(envelope.MessageId, out var state));
        Assert.Equal(1, state!.AttemptCount);
        Assert.NotNull(state.NextAttemptAt);
        Assert.True(state.NextAttemptAt > h.TimeProvider.GetUtcNow());
        Assert.Empty(h.DeadLetters.Keys);
    }

    [Fact]
    public async Task DeliverPendingMessagesAsync_WhenReceiverReportsRouteNotFound_RecordsFailureReasonFromResult()
    {
        var h = CreateOutbox();
        var envelope = CreateEnvelope();
        SendAndMarkDurable(h.Outbox, envelope);
        h.Extension.NextResult = DeliveryResult.RouteNotFound(RouteKey);

        await h.Outbox.DeliverPendingMessagesAsync();

        Assert.True(h.MessageStates.TryGetValue(envelope.MessageId, out var state));
        Assert.Equal(1, state!.AttemptCount);
        Assert.Contains(RouteKey, state.LastError);
        // Not yet dead-lettered: only one attempt against a high MaxDeliveryAttempts and a long MaxOutboxRetryAge.
        Assert.True(h.Outbox.TryGetMessage(envelope.MessageId, out _));
        Assert.Empty(h.DeadLetters.Keys);
    }

    [Fact]
    public async Task DeliverPendingMessagesAsync_WhenReceiverThrows_RecordsFailureViaCatchBranchAndDoesNotThrow()
    {
        var h = CreateOutbox();
        var envelope = CreateEnvelope();
        SendAndMarkDurable(h.Outbox, envelope);
        h.Extension.Throw = new InvalidOperationException("simulated delivery failure");

        // Must not propagate: the catch (Exception ex) when (ex is not OperationCanceledException) branch handles it.
        await h.Outbox.DeliverPendingMessagesAsync();

        Assert.True(h.MessageStates.TryGetValue(envelope.MessageId, out var state));
        Assert.Equal(1, state!.AttemptCount);
        Assert.Contains("simulated delivery failure", state.LastError);
        Assert.True(h.Outbox.TryGetMessage(envelope.MessageId, out _));
    }

    [Fact]
    public async Task DeliverPendingMessagesAsync_WhenStatusIsUnhandledByExplicitCases_TakesDefaultBranchAndCanDeadLetter()
    {
        // DeliveryStatus.Pending has no explicit switch case in DeliverPendingMessagesAsync, so it must
        // fall through to the `default:` branch, which records a failure with a message naming the status.
        // Using MaxDeliveryAttempts = 1 additionally exercises RecordDeliveryFailure's dead-letter transition
        // triggered by attempt count (as opposed to message age).
        var h = CreateOutbox(maxDeliveryAttempts: 1);
        var envelope = CreateEnvelope();
        SendAndMarkDurable(h.Outbox, envelope);
        h.Extension.NextResult = DeliveryResult.Pending();

        await h.Outbox.DeliverPendingMessagesAsync();

        // Dead-lettered: removed from the live outbox and recorded with the unexpected-status reason.
        Assert.False(h.Outbox.TryGetMessage(envelope.MessageId, out _));
        Assert.Empty(h.Outbox.Messages);
        Assert.True(h.DeadLetters.TryGetValue(envelope.MessageId, out var deadLetter));
        Assert.Contains("Pending", deadLetter!.Reason);
        Assert.Equal(1, deadLetter.AttemptCount);
        Assert.True(deadLetter.Envelope.Data.TryGetBody<string>(out var body));
        Assert.Equal("payload", body);
        // The message state itself is cleaned up as part of RemoveMessage.
        Assert.False(h.MessageStates.ContainsKey(envelope.MessageId));
    }

    [Fact]
    public async Task DeliverPendingMessagesAsync_WhenBackpressuredRepeatedly_AppliesExponentialBackoffAndOnlyRetriesAfterDelayElapses()
    {
        var backpressureDelay = TimeSpan.FromSeconds(2);
        var h = CreateOutbox(maxDeliveryAttempts: 100, backpressureRetryDelay: backpressureDelay);
        var envelope = CreateEnvelope();
        SendAndMarkDurable(h.Outbox, envelope);
        h.Extension.NextResult = DeliveryResult.Backpressured();

        // First attempt: backpressured, attempt #1, next retry scheduled ~2s out.
        await h.Outbox.DeliverPendingMessagesAsync();
        Assert.Single(h.Extension.DeliveredEnvelopes);
        h.MessageStates.TryGetValue(envelope.MessageId, out var afterFirst);
        var firstNextAttempt = afterFirst!.NextAttemptAt!.Value;
        Assert.Equal(h.TimeProvider.GetUtcNow() + backpressureDelay, firstNextAttempt);

        // Immediately retrying (time unchanged) must skip the message: it is not yet eligible for retry.
        await h.Outbox.DeliverPendingMessagesAsync();
        Assert.Single(h.Extension.DeliveredEnvelopes);
        h.MessageStates.TryGetValue(envelope.MessageId, out var stillFirst);
        Assert.Equal(1, stillFirst!.AttemptCount);

        // Advance time past the scheduled retry: the message becomes eligible again, doubling the backoff.
        h.TimeProvider.Advance(backpressureDelay + TimeSpan.FromMilliseconds(1));
        await h.Outbox.DeliverPendingMessagesAsync();
        Assert.Equal(2, h.Extension.DeliveredEnvelopes.Count);
        h.MessageStates.TryGetValue(envelope.MessageId, out var afterSecond);
        Assert.Equal(2, afterSecond!.AttemptCount);
        var secondNextAttempt = afterSecond.NextAttemptAt!.Value;
        Assert.Equal(h.TimeProvider.GetUtcNow() + (backpressureDelay * 2), secondNextAttempt);
    }

    [Fact]
    public async Task DeliverPendingMessagesAsync_WhenMessageAgeExceedsMaxRetryAge_DeadLettersDespiteLowAttemptCount()
    {
        // MaxDeliveryAttempts is generous (100) so the attempt-count branch of RecordDeliveryFailure cannot
        // fire; only the age-based OR clause (`now - envelope.CreatedAt >= _maxRetryAge`) can explain dead-lettering.
        var h = CreateOutbox(maxDeliveryAttempts: 100, maxRetryAge: TimeSpan.FromMinutes(5));
        var envelope = CreateEnvelope();
        SendAndMarkDurable(h.Outbox, envelope);

        // The envelope's CreatedAt was stamped using the real system clock at Build() time; advance our fake
        // clock (used for `now` inside RecordDeliveryFailure) far beyond MaxOutboxRetryAge.
        h.TimeProvider.SetUtcNow(envelope.CreatedAt.AddDays(1));
        h.Extension.NextResult = DeliveryResult.Backpressured();

        await h.Outbox.DeliverPendingMessagesAsync();

        Assert.False(h.Outbox.TryGetMessage(envelope.MessageId, out _));
        Assert.True(h.DeadLetters.TryGetValue(envelope.MessageId, out var deadLetter));
        Assert.Equal(1, deadLetter!.AttemptCount);
        Assert.Contains("backpressured", deadLetter.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeliverPendingMessagesAsync_RespectsBatchSizeAndInitiallyDefersUncommittedMessages()
    {
        var h = CreateOutbox(batchSize: 2);
        var durable = Enumerable.Range(0, 3).Select(_ => CreateEnvelope()).ToArray();
        foreach (var envelope in durable)
        {
            SendAndMarkDurable(h.Outbox, envelope);
        }

        var uncommitted = CreateEnvelope();
        h.Outbox.Send(uncommitted);

        await h.Outbox.DeliverPendingMessagesAsync();

        Assert.Equal(2, h.Extension.DeliveredEnvelopes.Count);
        Assert.All(h.Extension.DeliveredEnvelopes, envelope => Assert.Contains(envelope, durable));
        Assert.Single(durable, envelope => h.Outbox.TryGetMessage(envelope.MessageId, out _));
        Assert.True(h.Outbox.TryGetMessage(uncommitted.MessageId, out _));

        await h.Outbox.DeliverPendingMessagesAsync();

        Assert.Equal(4, h.Extension.DeliveredEnvelopes.Count);
        Assert.Contains(h.Extension.DeliveredEnvelopes, envelope => envelope.MessageId == uncommitted.MessageId);
        Assert.False(h.Outbox.TryGetMessage(uncommitted.MessageId, out _));
    }

    [Fact]
    public async Task DeliverPendingMessagesAsync_WhenReceiverReportsDuplicate_RemovesMessage()
    {
        var h = CreateOutbox();
        var envelope = CreateEnvelope();
        SendAndMarkDurable(h.Outbox, envelope);
        h.Extension.NextResult = DeliveryResult.Duplicate();

        await h.Outbox.DeliverPendingMessagesAsync();

        Assert.Single(h.Extension.DeliveredEnvelopes);
        Assert.False(h.Outbox.TryGetMessage(envelope.MessageId, out _));
        Assert.False(h.MessageStates.ContainsKey(envelope.MessageId));
        Assert.Empty(h.DeadLetters);
    }

    [Fact]
    public async Task ExecuteJobAsync_WhenNoMessagesPending_CompletesWithoutClaimingJobId()
    {
        var h = CreateOutbox(initialJobId: null);
        var context = new TestJobRunContext("job-1");

        var result = await h.Outbox.ExecuteJobAsync(context, CancellationToken.None);

        Assert.Equal(DurableJobRunStatus.Completed, result.Status);
        Assert.True(string.IsNullOrEmpty(h.JobId.Value));
        Assert.Empty(h.Extension.DeliveredEnvelopes);
    }

    [Fact]
    public async Task ExecuteJobAsync_WhenAnotherJobAlreadyOwnsTheOutbox_CompletesWithoutProcessing()
    {
        var h = CreateOutbox(initialJobId: "owning-job-id");
        var context = new TestJobRunContext("different-job-id");

        var result = await h.Outbox.ExecuteJobAsync(context, CancellationToken.None);

        Assert.Equal(DurableJobRunStatus.Completed, result.Status);
        Assert.Equal("owning-job-id", h.JobId.Value);
        Assert.Empty(h.Extension.DeliveredEnvelopes);
    }

    [Fact]
    public async Task ExecuteJobAsync_WhenJobIdUnclaimedAndMessagePending_ClaimsJobIdDeliversMessageAndReleasesJobId()
    {
        // The message is Send()'d without a prior OnWriteCompleted call. ExecuteJobAsync's own claim step
        // persists ALL registered state via _stateManager.WriteStateAsync (not just the job id), which is
        // what flushes DurableOutbox's pending-message set -- so the message becomes durable and eligible
        // for delivery within this same ExecuteJobAsync call. This exercises the "claims an unowned job id"
        // branch specifically (the other ExecuteJobAsync tests pre-seed a matching job id and so never take it).
        var h = CreateOutbox(initialJobId: null);
        var envelope = CreateEnvelope();
        h.Outbox.Send(envelope);
        Assert.Single(h.Outbox.Messages);
        var context = new TestJobRunContext("claimer-job");

        var result = await h.Outbox.ExecuteJobAsync(context, CancellationToken.None);

        Assert.Single(h.Extension.DeliveredEnvelopes);
        Assert.False(h.Outbox.TryGetMessage(envelope.MessageId, out _));
        Assert.Equal(DurableJobRunStatus.Completed, result.Status);
        // Job id is released again once the outbox drains to empty.
        Assert.True(string.IsNullOrEmpty(h.JobId.Value));
    }

    [Fact]
    public async Task ExecuteJobAsync_WhenMessageRemainsAfterBackpressure_ReturnsRetryAtNextAttemptTimeAndRetainsJobId()
    {
        var h = CreateOutbox(initialJobId: "job-42");
        var envelope = CreateEnvelope();
        SendAndMarkDurable(h.Outbox, envelope);
        h.Extension.NextResult = DeliveryResult.Backpressured();
        var context = new TestJobRunContext("job-42");

        var result = await h.Outbox.ExecuteJobAsync(context, CancellationToken.None);

        Assert.Equal(DurableJobRunStatus.RescheduleRequested, result.Status);
        h.MessageStates.TryGetValue(envelope.MessageId, out var state);
        Assert.Equal(state!.NextAttemptAt, result.RescheduleTime);
        Assert.Equal("job-42", h.JobId.Value);
        Assert.Single(h.Extension.DeliveredEnvelopes);
    }

    [Fact]
    public async Task ExecuteJobAsync_WhenAllMessagesDelivered_CompletesAndReleasesJobId()
    {
        var h = CreateOutbox(initialJobId: "job-7");
        var envelope = CreateEnvelope();
        SendAndMarkDurable(h.Outbox, envelope);
        h.Extension.NextResult = DeliveryResult.Processed();
        var context = new TestJobRunContext("job-7");

        var result = await h.Outbox.ExecuteJobAsync(context, CancellationToken.None);

        Assert.Equal(DurableJobRunStatus.Completed, result.Status);
        Assert.True(string.IsNullOrEmpty(h.JobId.Value));
        Assert.False(h.Outbox.TryGetMessage(envelope.MessageId, out _));
        Assert.Single(h.Extension.DeliveredEnvelopes);
    }

    // ----- Fakes -----

    private sealed class TestInboxExtension : IDurableInboxExtension
    {
        public DeliveryResult NextResult { get; set; } = DeliveryResult.Accepted();
        public Exception? Throw { get; set; }
        public List<DurableEnvelope> DeliveredEnvelopes { get; } = new();

        public ValueTask<DeliveryResult> DeliverAsync(DurableEnvelope envelope, DeliveryOptions options, CancellationToken cancellationToken = default)
        {
            DeliveredEnvelopes.Add(envelope);
            if (Throw is { } ex)
            {
                throw ex;
            }

            return ValueTask.FromResult(NextResult);
        }
    }

    private sealed class TestGrainFactory(IDurableInboxExtension extension) : IGrainFactory
    {
        public IDurableOutboxCommitExtension? OutboxCommitExtension { get; set; }

        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable
        {
            if (typeof(TGrainInterface) == typeof(IDurableInboxExtension))
            {
                return (TGrainInterface)(object)extension;
            }

            if (typeof(TGrainInterface) == typeof(IDurableOutboxCommitExtension))
            {
                return (TGrainInterface)(object)OutboxCommitExtension!;
            }

            throw new NotSupportedException($"Unexpected grain interface requested: {typeof(TGrainInterface)}");
        }

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithStringKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
    }

    private sealed class NoOpGrainLifecycle : IGrainLifecycle
    {
        public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer) => NoOpDisposable.Instance;
        public void AddMigrationParticipant(IGrainMigrationParticipant participant) { }
        public void RemoveMigrationParticipant(IGrainMigrationParticipant participant) { }

        private sealed class NoOpDisposable : IDisposable
        {
            public static readonly NoOpDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class MockGrainContext : IGrainContext
    {
        private readonly IGrainLifecycle _lifecycle = new NoOpGrainLifecycle();

        public GrainId GrainId { get; } = GrainId.Create("outbox-owner", Guid.NewGuid().ToString());
        public GrainReference GrainReference => throw new NotSupportedException();
        public object? GrainInstance => throw new NotSupportedException();
        public ActivationId ActivationId => throw new NotSupportedException();
        public GrainAddress Address => throw new NotSupportedException();
        public IServiceProvider ActivationServices => throw new NotSupportedException();
        public IGrainLifecycle ObservableLifecycle => _lifecycle;
        public IWorkItemScheduler Scheduler => throw new NotSupportedException();
        public PlacementStrategy PlacementStrategy => throw new NotSupportedException();
        public Task Deactivated => Task.CompletedTask;
        public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken) { }
        public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken) { }
        public TComponent? GetComponent<TComponent>() where TComponent : class => null;
        public object? GetComponent(Type type) => null;
        public TTarget? GetTarget<TTarget>() where TTarget : class => null;
        public object? GetTarget() => null;
        public void SetComponent<TComponent>(TComponent? instance) where TComponent : class { }
        public void ReceiveMessage(object message) { }
        public void Rehydrate(IRehydrationContext context) { }
        public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken) { }
        public bool Equals(IGrainContext? other) => ReferenceEquals(this, other);
    }

    private sealed class TestGrainLifecycle(ILogger logger) : LifecycleSubject(logger), IGrainLifecycle
    {
        public void AddMigrationParticipant(IGrainMigrationParticipant participant) { }
        public void RemoveMigrationParticipant(IGrainMigrationParticipant participant) { }
    }

    private sealed class TestDurableValue<T> : IDurableValue<T>
    {
        public T? Value { get; set; }
    }

    private sealed class TestJobHandlerRegistry : IDurableJobHandlerRegistry
    {
        public IDurableJobFeatureHandler? Handler { get; private set; }
        public void Register(string jobName, IDurableJobFeatureHandler handler, bool requiresTurnIsolation = false) => Handler = handler;
    }

    /// <summary>
    /// Unlike a fully wired job manager, this fake never re-invokes the handler: it just deterministically
    /// hands back a completed job descriptor. This exists solely so <see cref="DurableOutbox"/>'s constructor
    /// and its OnWriteCompleted-triggered fire-and-forget scheduling path (guarded, in tests, by pre-seeding
    /// the job id) have something non-null to call if they ever do run.
    /// </summary>
    private sealed class TestJobManager : ILocalDurableJobManager
    {
        public Task<DurableJob> ScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new DurableJob
            {
                Id = Guid.NewGuid().ToString(),
                Name = request.JobName,
                DueTime = request.DueTime,
                TargetGrainId = request.Target,
                ShardId = "test"
            });

        public Task<bool> TryCancelDurableJobAsync(DurableJob job, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class TestJobRunContext(string jobId) : IJobRunContext
    {
        public DurableJob Job { get; } = new DurableJob
        {
            Id = jobId,
            Name = DurableOutbox.JobName,
            DueTime = DateTimeOffset.UtcNow,
            TargetGrainId = default,
            ShardId = "test"
        };
        public string RunId { get; } = Guid.NewGuid().ToString();
        public int DequeueCount { get; } = 0;
    }

    private sealed class TestDurableDictionary<TKey, TValue> : IDurableDictionary<TKey, TValue> where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _dict = new();

        public TValue this[TKey key]
        {
            get => _dict[key];
            set => _dict[key] = value;
        }

        public ICollection<TKey> Keys => _dict.Keys;
        public ICollection<TValue> Values => _dict.Values;
        public int Count => _dict.Count;
        public bool IsReadOnly => false;

        public void Add(TKey key, TValue value) => _dict.Add(key, value);
        public void Add(KeyValuePair<TKey, TValue> item) => _dict.Add(item.Key, item.Value);
        public void Clear() => _dict.Clear();
        public bool Contains(KeyValuePair<TKey, TValue> item) => ((IDictionary<TKey, TValue>)_dict).Contains(item);
        public bool ContainsKey(TKey key) => _dict.ContainsKey(key);
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => ((IDictionary<TKey, TValue>)_dict).CopyTo(array, arrayIndex);
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _dict.GetEnumerator();
        public bool Remove(TKey key) => _dict.Remove(key);
        public bool Remove(KeyValuePair<TKey, TValue> item) => ((IDictionary<TKey, TValue>)_dict).Remove(item);
        public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => _dict.TryGetValue(key, out value);
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _dict.GetEnumerator();
    }
}

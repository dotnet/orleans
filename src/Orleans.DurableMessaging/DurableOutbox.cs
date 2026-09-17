using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;
using Orleans.Timers;

namespace Orleans.DurableMessaging;

/// <summary>
/// Prepares outbound intents and delivery outcomes for an admitted journal operation.
/// The messaging composite prepares prerequisites before synchronous finalization and capture.
/// </summary>
internal sealed partial class DurableOutbox : IDurableOutbox, IDurableJobFeatureHandler, ILifecycleObserver, IJournaledStateObserver
{
    internal const string JobName = "orleans.messaging.outbox-flush";
    public bool CanHandle(string jobName) => string.Equals(jobName, JobName, StringComparison.Ordinal);

    private readonly IJournaledStateManager _stateManager;
    private readonly IDurableDictionary<Guid, DurableEnvelope> _messages;
    private readonly IGrainFactory _grainFactory;
    private readonly IGrainContext _grainContext;
    private readonly ITimerRegistry _timerRegistry;
    private readonly ILogger<DurableOutbox> _logger;
    private readonly DurableMessagingInstruments _instruments;
    private readonly TimeSpan _backpressureRetryDelay;
    private readonly TimeSpan _maxRetryAge;
    private readonly int _maxDeliveryAttempts;
    private readonly int _batchSize;
    private readonly TimeSpan _deadLetterRetentionPeriod;
    private readonly int _maxRetainedDeadLetters;
    private readonly IDurableDictionary<Guid, OutboxMessageState> _messageStates;
    private readonly IDurableDictionary<Guid, OutboxDeadLetter> _deadLetters;
    private readonly IDurableValue<string> _jobId;
    private readonly IDurableValue<DurableJob> _job;
    private readonly IDurableValue<string> _completedJobId;
    private readonly IDurableValue<long> _jobSequence;
    private readonly ILocalDurableJobManager _jobManager;
    private readonly TimeProvider _jobTimeProvider;
    private readonly DurableMessagingPumpResults _pumpResults;
    private readonly DurableMessagingPumpCoordinator _pumpCoordinator = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _deliveryGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private readonly Dictionary<Guid, PendingMessage> _pendingMessages = [];
    private readonly List<OutboxWrite> _pendingWrites = [];
    private PendingMessage[] _admittedMessages = [];
    private OutboxWrite[] _admittedWrites = [];
    private PreparedDelivery[] _preparedDeliveries = [];
    private Dictionary<Guid, OutboxDeadLetter>? _preparedDeadLetters;
    private OwnershipProposal? _preparedOwnership;
    private string? _preparingOwnershipId;
    private PumpOwner _preparedOwner;
    private bool _preparing;
    private string? _committingOwnershipId;
    private DurableJob? _committingJob;
    private string? _committingCompletedJobId;
    private string? _durableOwnershipId;
    private DurableJob? _durableJob;
    private string? _durableCompletedJobId;
    private PendingDeliveryBatch? _pendingDeliveryBatch;
    private bool _recoveryCompleted;
    private int _ensureJobScheduledQueued;
    private int _activePumpTurns;
    private string _ownershipEpoch = Guid.NewGuid().ToString("N");
    private long _reservedSequence;
    private long _stateGeneration;
    private string? _ownershipStateError;
    private ExceptionDispatchInfo? _failure;
    private int _metricsActive;
    private int _reportedDepth;

    public DurableOutbox(
        IJournaledStateManager manager,
        [FromKeyedServices(DurableMessagingStateNames.Outbox)] IDurableDictionary<Guid, DurableEnvelope> messages,
        IGrainFactory grainFactory,
        IGrainContext grainContext,
        ITimerRegistry timerRegistry,
        ILogger<DurableOutbox> logger,
        DurableMessagingInstruments instruments,
        [FromKeyedServices(DurableMessagingStateNames.OutboxMessageState)] IDurableDictionary<Guid, OutboxMessageState> messageStates,
        [FromKeyedServices(DurableMessagingStateNames.OutboxDeadLetters)] IDurableDictionary<Guid, OutboxDeadLetter> deadLetters,
        [FromKeyedServices(DurableMessagingStateNames.OutboxJobId)] IDurableValue<string> jobId,
        [FromKeyedServices(DurableMessagingStateNames.OutboxJobHandle)] IDurableValue<DurableJob> job,
        [FromKeyedServices(DurableMessagingStateNames.OutboxCompletedJobId)] IDurableValue<string> completedJobId,
        [FromKeyedServices(DurableMessagingStateNames.OutboxJobSequence)] IDurableValue<long> jobSequence,
        ILocalDurableJobManager jobManager,
        IDurableJobHandlerRegistry jobHandlers,
        DurableMessagingPumpResults pumpResults,
        [FromKeyedServices(DurableJobTimeProviderNames.DurableJobs)] TimeProvider jobTimeProvider,
        IOptions<DurableInboxOptions> options)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(grainFactory);
        ArgumentNullException.ThrowIfNull(grainContext);
        ArgumentNullException.ThrowIfNull(timerRegistry);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(messageStates);
        ArgumentNullException.ThrowIfNull(deadLetters);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(completedJobId);
        ArgumentNullException.ThrowIfNull(jobSequence);
        ArgumentNullException.ThrowIfNull(jobManager);
        ArgumentNullException.ThrowIfNull(jobHandlers);
        ArgumentNullException.ThrowIfNull(pumpResults);
        ArgumentNullException.ThrowIfNull(jobTimeProvider);
        ArgumentNullException.ThrowIfNull(options);
        _stateManager = manager;
        _messages = messages;
        _grainFactory = grainFactory;
        _grainContext = grainContext;
        _timerRegistry = timerRegistry;
        _logger = logger;
        _instruments = instruments;
        _messageStates = messageStates;
        _deadLetters = deadLetters;
        _jobId = jobId;
        _job = job;
        _completedJobId = completedJobId;
        _jobSequence = jobSequence;
        _jobManager = jobManager;
        _pumpResults = pumpResults;
        _jobTimeProvider = jobTimeProvider;
        _backpressureRetryDelay = options.Value.BackpressureRetryDelay;
        _maxRetryAge = options.Value.MaxOutboxRetryAge;
        _maxDeliveryAttempts = options.Value.MaxDeliveryAttempts;
        _batchSize = options.Value.OutboxBatchSize;
        _deadLetterRetentionPeriod = options.Value.DeadLetterRetentionPeriod;
        _maxRetainedDeadLetters = options.Value.MaxRetainedDeadLetters;
        jobHandlers.Register(this);

        var lifecycle = grainContext.ObservableLifecycle;
        lifecycle.Subscribe(RuntimeTypeNameFormatter.Format(GetType()), GrainLifecycleStage.Activate, this);
    }

    public int Count => _messages.Count + _pendingMessages.Keys.Count(key => !_messages.ContainsKey(key));

    public IEnumerable<DurableEnvelope> Messages => _messages.Values.Concat(
        _pendingMessages.Where(pair => !_messages.ContainsKey(pair.Key)).Select(static pair => pair.Value.Envelope));

    public bool TryGetMessage(Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope)
    {
        if (_pendingMessages.TryGetValue(messageId, out var pending))
        {
            envelope = pending.Envelope;
            return true;
        }
        return _messages.TryGetValue(messageId, out envelope);
    }

    public void Send(DurableEnvelope envelope)
    {
        ValidateReady();
        if (envelope.SenderId != _grainContext.GrainId)
        {
            throw new InvalidOperationException(
                $"Durable outbox sender '{envelope.SenderId}' does not match the owning grain '{_grainContext.GrainId}'.");
        }
        if (TryGetMessage(envelope.MessageId, out var existing))
        {
            if (!DurableEnvelopeEquivalence.AreEquivalent(existing, envelope))
            {
                throw new InvalidOperationException(
                    $"The durable outbox already contains a different envelope with message ID '{envelope.MessageId}'.");
            }
            return;
        }
        var state = new OutboxMessageState { EnqueuedAt = _jobTimeProvider.GetUtcNow() };
        _pendingMessages.Add(envelope.MessageId, new(envelope, state));
        EnsureMetricsActive();
        ReconcileOutboxDepth();
        _instruments.OnOutboxMessageSent(_grainContext.GrainId.Type.ToString(), envelope.RouteKey);
    }

    public void OnWriteRequested() => ValidateReady();

    public void OnDeleteRequested()
    {
        ValidateReady();
        if (_preparing || _pendingWrites.Count != 0 || _admittedWrites.Length != 0
            || _activePumpTurns != 0 || _ensureJobScheduledQueued != 0
            || _pendingDeliveryBatch is not null || _deliveryGate.CurrentCount == 0 || _gate.CurrentCount == 0)
        {
            throw new InvalidOperationException("Durable outbox operations must be quiescent before deleting journaled state.");
        }
    }

    public ValueTask OnDeletePreparingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OnDeleteRequested();
        return default;
    }

    public async ValueTask OnWritePreparingAsync(CancellationToken cancellationToken)
    {
        ValidateReady();
        _preparing = true;
        _preparedOwner = CurrentOwner;
        _admittedMessages = _pendingMessages.Values.ToArray();
        _admittedWrites = _pendingWrites.ToArray();
        _pendingWrites.Clear();
        _preparedOwnership = null;
        _preparedDeadLetters = null;
        var deliveries = new List<PreparedDelivery>();
        foreach (var operation in _admittedWrites)
        {
            ValidateGeneration(operation.Generation);
            if (operation is DeliveryWrite delivery)
            {
                ValidateOwner(delivery.Owner);
                foreach (var outcome in delivery.Outcomes)
                {
                    ValidateCandidate(outcome.Candidate);
                    deliveries.Add(PrepareDelivery(outcome));
                }
            }
            else if (operation is ClearOwnerWrite clear)
            {
                ValidateOwner(clear.Owner);
            }
            else if (operation is CompactWrite)
            {
                PrepareDeadLetters();
                DurableDeadLetterRetention.Compact(_preparedDeadLetters!, _jobTimeProvider.GetUtcNow(),
                    _deadLetterRetentionPeriod, _maxRetainedDeadLetters, static entry => entry.DeadLetteredAt);
            }
        }
        _preparedDeliveries = deliveries.ToArray();

        var hasWork = _messages.Count + _admittedMessages.Length > deliveries.Count(static result => result.Remove);
        if (hasWork && (!HasCommittedOwnership() || _admittedWrites.OfType<OwnershipWrite>().Any(static write => write.ReplaceExisting)))
        {
            var sequence = checked(++_reservedSequence);
            var id = DurableMessagingJobOwnership.CreateId(_ownershipEpoch, sequence);
            _preparingOwnershipId = id;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            var job = await _jobManager.ScheduleJobAsync(new ScheduleJobRequest
            {
                Target = _grainContext.GrainId,
                JobName = JobName,
                DueTime = _jobTimeProvider.GetUtcNow(),
                Metadata = DurableMessagingJobOwnership.CreateMetadata(id)
            }, cancellation.Token).ConfigureAwait(true);
            ValidateOwner(_preparedOwner);
            _preparedOwnership = new(id, sequence, job);
        }
    }

    public void FinalizeWrite(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOwner(_preparedOwner);
        foreach (var message in _admittedMessages)
        {
            if (_messages.ContainsKey(message.Envelope.MessageId))
            {
                throw new InvalidOperationException("An admitted outbox intent already exists in journaled state.");
            }
        }
        foreach (var result in _preparedDeliveries)
        {
            ValidateCandidate(result.Outcome.Candidate);
        }
        var hasWork = _messages.Count + _admittedMessages.Length > _preparedDeliveries.Count(static result => result.Remove);
        if (hasWork && _preparedOwnership is null && !HasCommittedOwnership())
        {
            throw new InvalidOperationException("Pending outbox messages require acknowledged durable job ownership before capture.");
        }
        if (_preparedOwnership is { } owner)
        {
            _jobId.Value = owner.Id;
            _job.Value = owner.Job;
            _jobSequence.Value = owner.Sequence;
        }
        foreach (var message in _admittedMessages)
        {
            _messages.Add(message.Envelope.MessageId, message.Envelope);
            _messageStates.Add(message.Envelope.MessageId, message.State);
        }
        foreach (var result in _preparedDeliveries)
        {
            var envelope = result.Outcome.Candidate.Envelope;
            if (result.Remove)
            {
                _messages.Remove(envelope.MessageId);
                _messageStates.Remove(envelope.MessageId);
            }
            else
            {
                _messageStates[envelope.MessageId] = result.Retry!;
            }
            RecordDeliveryMetrics(result);
        }
        if (_preparedDeadLetters is { } deadLetters)
        {
            foreach (var key in _deadLetters.Keys.Where(key => !deadLetters.ContainsKey(key)).ToArray())
            {
                _deadLetters.Remove(key);
            }
            foreach (var entry in deadLetters)
            {
                if (!_deadLetters.TryGetValue(entry.Key, out var existing) || !ReferenceEquals(existing, entry.Value))
                {
                    _deadLetters[entry.Key] = entry.Value;
                }
            }
        }
        if (!hasWork && _pendingMessages.Count == 0 && _admittedWrites.OfType<ClearOwnerWrite>().FirstOrDefault() is { } clear)
        {
            _completedJobId.Value = clear.Owner.Id;
            _jobId.Value = null;
            _job.Value = null;
        }
        ReconcileOutboxDepth();
    }

    public void OnWriteStarted()
    {
        _committingOwnershipId = _jobId.Value;
        _committingJob = _job.Value;
        _committingCompletedJobId = _completedJobId.Value;
    }

    public void OnWriteCompleted()
    {
        _durableOwnershipId = _committingOwnershipId;
        _durableJob = _committingJob;
        _durableCompletedJobId = _committingCompletedJobId;
        foreach (var message in _admittedMessages)
        {
            _pendingMessages.Remove(message.Envelope.MessageId);
        }
        if (_preparedDeliveries.Length > 0)
        {
            var delivered = _preparedDeliveries.Count(static result => result.Outcome.Result?.Status is
                DeliveryStatus.Accepted or DeliveryStatus.Duplicate or DeliveryStatus.DeadLettered);
            var backpressured = _preparedDeliveries.Count(static result => result.Outcome.Result?.Status == DeliveryStatus.Backpressured);
            LogDeliveryComplete(_logger, delivered, backpressured, _preparedDeliveries.Length - delivered - backpressured, Count);
        }
        foreach (var operation in _admittedWrites)
        {
            operation.Completed.TrySetResult();
        }
        _admittedMessages = [];
        _admittedWrites = [];
        _preparedDeliveries = [];
        _preparedDeadLetters = null;
        _preparedOwnership = null;
        _preparingOwnershipId = null;
        _preparing = false;
    }

    public void OnFaulted(Exception exception)
    {
        _failure ??= ExceptionDispatchInfo.Capture(exception);
        try
        {
            Stop();
        }
        finally
        {
            foreach (var operation in _pendingWrites.Concat(_admittedWrites))
            {
                operation.Completed.TrySetException(exception);
            }
        }
    }

    public void OnRecoveryStarted() => _recoveryCompleted = false;

    public void OnRecoveryCompleted()
    {
        _stateGeneration++;
        _reservedSequence = _jobSequence.Value;
        _durableOwnershipId = _jobId.Value;
        _durableJob = _job.Value;
        _durableCompletedJobId = _completedJobId.Value;
        _ownershipStateError = DurableMessagingJobOwnership.GetPairError(_jobId.Value, _job.Value);
        _recoveryCompleted = true;
        ReconcileOutboxDepth();
        if (_messages.Count > 0 && _ownershipStateError is null && !HasCommittedOwnership())
        {
            QueueEnsureJobScheduled();
        }
    }

    public void OnDeleteCompleted()
    {
        _pumpCoordinator.Reset();
        _pumpResults.Clear(JobName);
        _stateGeneration++;
        _ownershipEpoch = Guid.NewGuid().ToString("N");
        _reservedSequence = _jobSequence.Value;
        _pendingMessages.Clear();
        _durableOwnershipId = null;
        _durableJob = null;
        _durableCompletedJobId = null;
        _ownershipStateError = null;
        ReconcileOutboxDepth();
    }

    private async ValueTask SubmitAsync(OutboxWrite operation)
    {
        ValidateReady();
        _pendingWrites.Add(operation);
        try
        {
            await Task.WhenAll(WriteAsync(), operation.Completed.Task).ConfigureAwait(true);
            _failure?.Throw();
        }
        finally
        {
            _pendingWrites.Remove(operation);
        }
        async Task WriteAsync()
        {
            try
            {
                await _stateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                operation.Completed.TrySetException(exception);
                throw;
            }
        }
    }

    private PumpOwner CurrentOwner => new(_jobId.Value, _job.Value, _stateGeneration);

    private void ValidateReady()
    {
        _failure?.Throw();
        _shutdown.Token.ThrowIfCancellationRequested();
        ThrowIfOwnershipStateInvalid();
        if (!_recoveryCompleted)
        {
            throw new InvalidOperationException("Durable outbox initialization has not completed.");
        }
    }

    private void ValidateGeneration(long generation)
    {
        ValidateReady();
        if (generation != _stateGeneration)
        {
            throw new InvalidOperationException("The admitted outbox operation belongs to an obsolete activation or deletion generation.");
        }
    }

    private void ValidateOwner(PumpOwner owner)
    {
        ValidateGeneration(owner.Generation);
        if (!string.Equals(owner.Id, _jobId.Value, StringComparison.Ordinal)
            || !(owner.Job is null && _job.Value is null || DurableMessagingJobOwnership.IsSamePhysicalJob(owner.Job, _job.Value)))
        {
            throw new InvalidOperationException("The admitted outbox operation no longer owns the acknowledged physical job.");
        }
    }

    private void ThrowIfOwnershipStateInvalid()
    {
        var error = _ownershipStateError ?? DurableMessagingJobOwnership.GetPairError(_jobId.Value, _job.Value);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }
    }

    private bool HasCommittedOwnership() => DurableMessagingJobOwnership.HasOwner(_jobId.Value, _job.Value)
        && string.Equals(_durableOwnershipId, _jobId.Value, StringComparison.Ordinal)
        && DurableMessagingJobOwnership.IsSamePhysicalJob(_durableJob, _job.Value);

    private bool IsCurrentPump(DurableJob job, long stateGeneration) => _failure is null && !_shutdown.IsCancellationRequested
        && _recoveryCompleted && stateGeneration == _stateGeneration
        && DurableMessagingJobOwnership.IsSamePhysicalJob(_job.Value, job) && HasCommittedOwnership();

    private bool IsOwnershipTransitionPending(string ownershipId) =>
        string.Equals(ownershipId, _preparingOwnershipId, StringComparison.Ordinal)
        || !string.Equals(_durableOwnershipId, _jobId.Value, StringComparison.Ordinal)
        || (_job.Value is not null && !DurableMessagingJobOwnership.IsSamePhysicalJob(_durableJob, _job.Value));

    private DeliveryCandidate[] SelectMessages()
    {
        var now = _jobTimeProvider.GetUtcNow();
        var selected = _messages.Values.Where(envelope => !_pendingMessages.ContainsKey(envelope.MessageId) && IsReadyForAttempt(envelope, now))
            .Take(_batchSize).Select(envelope => new DeliveryCandidate(envelope,
                _messageStates.TryGetValue(envelope.MessageId, out var state) ? CopyState(state) : null)).ToArray();
        if (selected.Length == 0)
        {
            LogNoDurableMessages(_logger, Count);
        }
        else
        {
            LogDeliveringMessages(_logger, selected.Length);
        }
        return selected;
    }

    private static OutboxMessageState CopyState(OutboxMessageState state) => new()
    {
        AttemptCount = state.AttemptCount,
        LastError = state.LastError,
        NextAttemptAt = state.NextAttemptAt,
        EnqueuedAt = state.EnqueuedAt
    };

    private void ValidateCandidate(DeliveryCandidate candidate)
    {
        if (_pendingMessages.ContainsKey(candidate.Envelope.MessageId)
            || !_messages.TryGetValue(candidate.Envelope.MessageId, out var current)
            || !DurableEnvelopeEquivalence.AreEquivalent(candidate.Envelope, current))
        {
            throw new InvalidOperationException("The admitted outbox delivery no longer refers to a durable pending message.");
        }
        _messageStates.TryGetValue(current.MessageId, out var state);
        var expected = candidate.State;
        if (state?.AttemptCount != expected?.AttemptCount || state?.NextAttemptAt != expected?.NextAttemptAt
            || state?.EnqueuedAt != expected?.EnqueuedAt || state?.LastError != expected?.LastError)
        {
            throw new InvalidOperationException("The admitted outbox delivery attempt state has changed.");
        }
    }

    private async Task<DeliveryOutcome> DeliverAsync(DeliveryCandidate candidate, CancellationToken cancellationToken)
    {
        if (candidate.State?.EnqueuedAt is { } enqueuedAt
            && DurableMessagingTime.IsExpired(_jobTimeProvider.GetUtcNow(), enqueuedAt, _maxRetryAge))
        {
            return new(candidate, null, $"The message exceeded the maximum retry age of {_maxRetryAge}.", TimeSpan.Zero, Expired: true);
        }
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var envelope = candidate.Envelope;
            var target = envelope.ReceiverId == _grainContext.GrainId
                ? _grainContext.GetGrainExtension<IDurableInboxExtension>()
                : _grainFactory.GetGrain<IDurableInboxExtension>(envelope.ReceiverId);
            var result = await target.DeliverAsync(envelope, cancellationToken).ConfigureAwait(true);
            return new(candidate, result, null, stopwatch.Elapsed, Expired: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogDeliveryError(_logger, exception, candidate.Envelope.MessageId, candidate.Envelope.SenderId,
                candidate.Envelope.ReceiverId, candidate.Envelope.RouteKey, candidate.Envelope.CorrelationKey?.ToString());
            return new(candidate, null, exception.ToString(), stopwatch.Elapsed, Expired: false);
        }
    }

    public async Task DeliverPendingMessagesAsync(CancellationToken cancellationToken = default)
    {
        await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            ValidateReady();
            var owner = CurrentOwner;
            var outcomes = new List<DeliveryOutcome>();
            foreach (var candidate in SelectMessages())
            {
                outcomes.Add(await DeliverAsync(candidate, cancellationToken).ConfigureAwait(true));
            }
            cancellationToken.ThrowIfCancellationRequested();
            ValidateOwner(owner);
            if (outcomes.Count > 0)
            {
                await SubmitAsync(new DeliveryWrite(owner, outcomes.ToArray())).ConfigureAwait(true);
            }
        }
        finally
        {
            _deliveryGate.Release();
        }
    }

    private async Task AdvancePendingDeliveriesAsync(DurableJob job, long generation,
        CancellationToken cancellationToken, CancellationToken attemptCancellationToken)
    {
        await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            ValidateReady();
            if (!IsCurrentPump(job, generation))
            {
                return;
            }
            if (_pendingDeliveryBatch is { } batch)
            {
                if (batch.Owner.Generation != generation || !DurableMessagingJobOwnership.IsSamePhysicalJob(batch.Owner.Job, job)
                    || batch.Cancellation.IsCancellationRequested)
                {
                    CancelPendingDeliveryBatch();
                    return;
                }
                if (batch.Attempts.Any(static task => !task.IsCompleted))
                {
                    return;
                }
                _pendingDeliveryBatch = null;
                using (batch)
                {
                    var outcomes = await Task.WhenAll(batch.Attempts).ConfigureAwait(true);
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateOwner(batch.Owner);
                    await SubmitAsync(new DeliveryWrite(batch.Owner, outcomes)).ConfigureAwait(true);
                }
                return;
            }

            var candidates = SelectMessages();
            if (candidates.Length == 0)
            {
                return;
            }
            var owner = new PumpOwner(_jobId.Value, job, generation);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(attemptCancellationToken, _shutdown.Token);
            var pending = new PendingDeliveryBatch(owner, cancellation);
            try
            {
                foreach (var candidate in candidates)
                {
                    if (candidate.Envelope.ReceiverId == _grainContext.GrainId)
                    {
                        var result = await DeliverAsync(candidate, cancellationToken).ConfigureAwait(true);
                        ValidateReady();
                        if (!IsCurrentPump(job, generation))
                        {
                            pending.Cancel();
                            return;
                        }
                        pending.Attempts.Add(Task.FromResult(result));
                    }
                    else
                    {
                        pending.Attempts.Add(DeliverAsync(candidate, cancellation.Token));
                    }
                }
                if (pending.Attempts.All(static task => task.IsCompleted))
                {
                    using (pending)
                    {
                        var outcomes = await Task.WhenAll(pending.Attempts).ConfigureAwait(true);
                        cancellationToken.ThrowIfCancellationRequested();
                        ValidateOwner(owner);
                        await SubmitAsync(new DeliveryWrite(owner, outcomes)).ConfigureAwait(true);
                    }
                }
                else
                {
                    _pendingDeliveryBatch = pending;
                }
            }
            catch
            {
                pending.Cancel();
                throw;
            }
        }
        finally
        {
            _deliveryGate.Release();
        }
    }

    private PreparedDelivery PrepareDelivery(DeliveryOutcome outcome)
    {
        var status = outcome.Result?.Status;
        if (status is DeliveryStatus.Accepted or DeliveryStatus.Duplicate or DeliveryStatus.DeadLettered)
        {
            return new(outcome, true, null);
        }
        var state = outcome.Candidate.State is { } existing ? CopyState(existing) : new OutboxMessageState();
        var now = _jobTimeProvider.GetUtcNow();
        if (!outcome.Expired)
        {
            state.AttemptCount = checked(state.AttemptCount + 1);
        }
        state.EnqueuedAt ??= now;
        state.LastError = outcome.Error ?? status switch
        {
            DeliveryStatus.Backpressured => "The receiver is backpressured.",
            DeliveryStatus.RouteNotFound => outcome.Result?.Message ?? "The receiver has no compatible route.",
            _ => $"Unexpected delivery status '{status}'."
        };
        if (outcome.Expired || state.AttemptCount >= _maxDeliveryAttempts
            || DurableMessagingTime.IsExpired(now, state.EnqueuedAt.Value, _maxRetryAge))
        {
            var letter = new OutboxDeadLetter
            {
                Envelope = outcome.Candidate.Envelope,
                DeadLetteredAt = now,
                Reason = state.LastError,
                AttemptCount = state.AttemptCount
            };
            PrepareDeadLetters();
            DurableDeadLetterRetention.Compact(_preparedDeadLetters!, now, _deadLetterRetentionPeriod,
                _maxRetainedDeadLetters, static entry => entry.DeadLetteredAt,
                reservedCapacity: _preparedDeadLetters!.ContainsKey(letter.Envelope.MessageId) ? 0 : 1);
            _preparedDeadLetters[letter.Envelope.MessageId] = letter;
            return new(outcome, true, null);
        }
        var exponent = Math.Min(state.AttemptCount - 1, DurableInboxOptions.MaximumBackoffExponent);
        state.NextAttemptAt = DurableMessagingTime.AddClamped(now, TimeSpan.FromTicks(_backpressureRetryDelay.Ticks * (1L << exponent)));
        return new(outcome, false, state);
    }

    private void PrepareDeadLetters() => _preparedDeadLetters ??= _deadLetters.ToDictionary(static pair => pair.Key, static pair => pair.Value);

    private void RecordDeliveryMetrics(PreparedDelivery delivery)
    {
        var outcome = delivery.Outcome;
        var envelope = outcome.Candidate.Envelope;
        var status = outcome.Result?.Status;
        var metricStatus = status == DeliveryStatus.RouteNotFound ? "route_not_found" : status?.ToString().ToLowerInvariant() ?? "error";
        _instruments.OnOutboxMessageDelivered(_grainContext.GrainId.Type.ToString(), envelope.RouteKey, metricStatus);
        _instruments.OnOutboxDeliveryDuration(outcome.Duration, _grainContext.GrainId.Type.ToString(), envelope.RouteKey);
        switch (status)
        {
            case DeliveryStatus.Accepted:
            case DeliveryStatus.Duplicate:
            case DeliveryStatus.DeadLettered:
                LogMessageDelivered(_logger, envelope.MessageId, envelope.SenderId, envelope.ReceiverId, envelope.RouteKey,
                    status.Value, envelope.CorrelationKey?.ToString());
                break;
            case DeliveryStatus.Backpressured:
                LogDeliveryBackpressured(_logger, envelope.MessageId, envelope.ReceiverId, envelope.RouteKey, envelope.CorrelationKey?.ToString());
                break;
            case DeliveryStatus.RouteNotFound:
                LogDeliveryRouteNotFound(_logger, envelope.MessageId, envelope.SenderId, envelope.ReceiverId,
                    envelope.RouteKey, envelope.CorrelationKey?.ToString(), outcome.Result?.Message);
                break;
            case { } unexpected:
                LogUnexpectedDeliveryStatus(_logger, unexpected, envelope.MessageId, envelope.RouteKey, envelope.CorrelationKey?.ToString());
                break;
        }
    }

    private bool IsReadyForAttempt(DurableEnvelope envelope, DateTimeOffset now) =>
        !_messageStates.TryGetValue(envelope.MessageId, out var state)
        || state.EnqueuedAt is { } enqueuedAt && DurableMessagingTime.IsExpired(now, enqueuedAt, _maxRetryAge)
        || state.NextAttemptAt is null || state.NextAttemptAt <= now;

    private DateTimeOffset? GetNextAttemptAt(DurableEnvelope envelope, DateTimeOffset now)
    {
        if (!_messageStates.TryGetValue(envelope.MessageId, out var state))
        {
            return null;
        }
        var retryAt = state.NextAttemptAt ?? now;
        var expiresAt = state.EnqueuedAt is { } enqueuedAt ? DurableMessagingTime.AddClamped(enqueuedAt, _maxRetryAge) : retryAt;
        return retryAt <= expiresAt ? retryAt : expiresAt;
    }

    private void CancelPendingDeliveryBatch()
    {
        if (_pendingDeliveryBatch is { } batch)
        {
            _pendingDeliveryBatch = null;
            batch.Cancel();
        }
    }

    public async Task OnStart(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReady();
        EnsureMetricsActive();
        if (_messages.Count > 0)
        {
            LogPumpStartingOnActivation(_logger, _messages.Count);
        }
        if (_messages.Count > 0 && !HasCommittedOwnership())
        {
            QueueEnsureJobScheduled();
        }
        if (_deadLetters.Count > _maxRetainedDeadLetters || _deadLetters.Values.Any(entry =>
            DurableMessagingTime.IsExpired(_jobTimeProvider.GetUtcNow(), entry.DeadLetteredAt, _deadLetterRetentionPeriod)))
        {
            await SubmitAsync(new CompactWrite(_stateGeneration)).ConfigureAwait(true);
        }
    }

    public Task OnStop(CancellationToken cancellationToken = default)
    {
        Stop();
        return Task.CompletedTask;
    }

    private void Stop()
    {
        _stateGeneration++;
        _pumpCoordinator.Reset();
        _pumpResults.Clear(JobName);
        try
        {
            _shutdown.Cancel();
        }
        finally
        {
            CancelPendingDeliveryBatch();
            if (Interlocked.Exchange(ref _metricsActive, 0) != 0)
            {
                _instruments.OnOutboxDepthChanged(-Interlocked.Exchange(ref _reportedDepth, 0));
            }
        }
    }

    private void EnsureMetricsActive()
    {
        if (Interlocked.Exchange(ref _metricsActive, 1) == 0)
        {
            _reportedDepth = Count;
            _instruments.OnOutboxDepthChanged(Count);
        }
    }

    private void ReconcileOutboxDepth()
    {
        if (Volatile.Read(ref _metricsActive) != 0)
        {
            var count = Count;
            _instruments.OnOutboxDepthChanged(count - Interlocked.Exchange(ref _reportedDepth, count));
        }
    }

    private void QueueEnsureJobScheduled()
    {
        if (Interlocked.Exchange(ref _ensureJobScheduledQueued, 1) != 0)
        {
            return;
        }
        var state = new EnsureJobTimerState(this);
        try
        {
            state.Handle.Attach(_timerRegistry.RegisterGrainTimer(_grainContext,
                static (state, cancellationToken) => state.RunAsync(cancellationToken), state,
                new GrainTimerCreationOptions(TimeSpan.Zero, Timeout.InfiniteTimeSpan) { Interleave = false, KeepAlive = true }));
        }
        catch
        {
            Volatile.Write(ref _ensureJobScheduledQueued, 0);
            throw;
        }
    }

    internal async Task EnsureJobScheduledAsync(bool replaceExisting, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            ValidateReady();
            if (_messages.Count > 0 && (replaceExisting || !HasCommittedOwnership()))
            {
                await SubmitAsync(new OwnershipWrite(_stateGeneration, replaceExisting)).ConfigureAwait(true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        _failure?.Throw();
        _shutdown.Token.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfOwnershipStateInvalid();
        if (!DurableMessagingJobOwnership.TryGetOwnershipId(context.Job, out var ownershipId))
        {
            return DurableJobRunResult.Completed;
        }

        if (IsOwnershipTransitionPending(ownershipId))
        {
            return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
        }

        var key = new DurableMessagingPumpExecutionKey(
            JobName,
            context.Job.Id,
            context.RunId,
            Volatile.Read(ref _stateGeneration));
        if (!string.Equals(_jobId.Value, ownershipId, StringComparison.Ordinal))
        {
            var disposition = DurableMessagingJobOwnership.ResolveMismatch(
                _recoveryCompleted,
                HasCommittedOwnership(),
                DurableMessagingJobOwnership.IsCompleted(_durableCompletedJobId, ownershipId),
                Count > 0);
            if (disposition == OwnershipMismatchDisposition.ReclaimOrphan)
            {
                LogOrphanedJobReclaimed(_logger, ownershipId, _grainContext.GrainId);
                _instruments.OnOrphanedJobReclaimed(_grainContext.GrainId.Type.ToString(), JobName);
                return CompleteObsoleteExecution(key);
            }

            if (disposition == OwnershipMismatchDisposition.CompleteStale)
            {
                return CompleteObsoleteExecution(key);
            }

            return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
        }

        if (!_recoveryCompleted || IsOwnershipTransitionPending(ownershipId))
        {
            return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
        }

        if (!DurableMessagingJobOwnership.IsSamePhysicalJob(_job.Value, context.Job))
        {
            return CompleteObsoleteExecution(key);
        }

        if (_pumpResults.TryTake(key, out var result, out var exception))
        {
            if (exception is not null)
            {
                throw exception;
            }

            return result!;
        }

        if (!_pumpCoordinator.TryAcquire(ownershipId, cancellationToken, out var lease))
        {
            return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
        }

        if (!_pumpResults.TryStart(key, cancellationToken, out var execution))
        {
            _pumpCoordinator.Release(lease);
            return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
        }

        _activePumpTurns++;
        var state = new PumpTimerState(
            this,
            execution,
            lease,
            context.Job,
            cancellationToken);
        try
        {
            state.Handle.Attach(_timerRegistry.RegisterGrainTimer(
                _grainContext,
                static (state, timerCancellation) => state.RunAsync(timerCancellation),
                state,
                new GrainTimerCreationOptions(TimeSpan.Zero, Timeout.InfiniteTimeSpan)
                {
                    Interleave = false,
                    KeepAlive = true
                }));
        }
        catch (Exception registrationException)
        {
            _activePumpTurns--;
            _pumpCoordinator.Release(lease);
            _pumpResults.Fail(execution, registrationException);
            throw;
        }

        return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
    }

    private DurableJobRunResult CompleteObsoleteExecution(DurableMessagingPumpExecutionKey key)
    {
        // Stable ownership retires this run; its retained result will no longer be polled.
        _pumpResults.TryTake(key, out _, out _);
        return DurableJobRunResult.Completed;
    }

    private async Task RunPumpTimerAsync(
        DurableMessagingPumpExecution execution,
        DurableMessagingPumpLease lease,
        DurableJob job,
        CancellationToken jobCancellation,
        CancellationToken timerCancellation)
    {
        if (!_pumpCoordinator.IsCurrent(lease) || !IsCurrentPump(job, execution.Key.StateGeneration))
        {
            _pumpResults.Discard(execution);
            return;
        }

        if (!_pumpResults.TryBegin(execution))
        {
            return;
        }

        DurableJobRunResult? result = null;
        Exception? failure = null;
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                jobCancellation,
                timerCancellation,
                _shutdown.Token);
            result = await ExecuteJobCoreAsync(
                lease.OwnershipId,
                job,
                execution.Key.StateGeneration,
                linkedCancellation.Token,
                jobCancellation);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (failure is null)
            {
                _pumpResults.Complete(execution, result!);
            }
            else
            {
                _pumpResults.Fail(execution, failure);
            }
        }
    }

    internal async ValueTask<DurableJobRunResult> ExecuteJobCoreAsync(string jobId, DurableJob job, long stateGeneration,
        CancellationToken cancellationToken, CancellationToken attemptCancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            ValidateReady();
            if (!IsCurrentPump(job, stateGeneration) || IsOwnershipTransitionPending(jobId))
            {
                return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
            }
        }
        finally
        {
            _gate.Release();
        }
        await AdvancePendingDeliveriesAsync(job, stateGeneration, cancellationToken, attemptCancellationToken).ConfigureAwait(true);
        if (_pendingDeliveryBatch is not null)
        {
            return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            ValidateReady();
            if (!IsCurrentPump(job, stateGeneration) || IsOwnershipTransitionPending(jobId))
            {
                return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
            }
            if (Count == 0)
            {
                await SubmitAsync(new ClearOwnerWrite(new(jobId, job, stateGeneration))).ConfigureAwait(true);
                if (_jobId.Value is null)
                {
                    return DurableJobRunResult.Completed;
                }
            }
            if (_pendingMessages.Count > 0)
            {
                return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
            }
            var now = _jobTimeProvider.GetUtcNow();
            var attempts = _messages.Values.Select(envelope => GetNextAttemptAt(envelope, now)).ToArray();
            var next = attempts.Any(value => value is null || value <= now) ? now : attempts.Min() ?? now;
            return DurableJobRunResult.RescheduleAt(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "No durable messages to deliver (all {Count} messages are still pending)")]
    private static partial void LogNoDurableMessages(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Delivering {Count} durable messages from outbox")]
    private static partial void LogDeliveringMessages(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Delivered message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}' (Status: {Status}, CorrelationKey: {CorrelationKey})")]
    private static partial void LogMessageDelivered(ILogger logger, Guid messageId, GrainId senderId, GrainId receiverId, string routeKey, DeliveryStatus status, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Route not found for message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey}): {Message}")]
    private static partial void LogDeliveryRouteNotFound(ILogger logger, Guid messageId, GrainId senderId, GrainId receiverId, string routeKey, string? correlationKey, string? message);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Backpressured delivering message {MessageId} to {ReceiverId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey}), will retry later")]
    private static partial void LogDeliveryBackpressured(ILogger logger, Guid messageId, GrainId receiverId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Unexpected delivery status {Status} for message {MessageId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogUnexpectedDeliveryStatus(ILogger logger, DeliveryStatus status, Guid messageId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error delivering message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogDeliveryError(ILogger logger, Exception exception, Guid messageId, GrainId senderId, GrainId receiverId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Outbox delivery complete: {DeliveredCount} delivered, {BackpressuredCount} backpressured, {FailedCount} failed, {RemainingCount} remaining")]
    private static partial void LogDeliveryComplete(ILogger logger, int deliveredCount, int backpressuredCount, int failedCount, int remainingCount);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Grain activated with {Count} pending outbox messages, starting pump")]
    private static partial void LogPumpStartingOnActivation(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error in outbox pump loop")]
    private static partial void LogPumpLoopError(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Reclaimed orphaned outbox job ownership {OwnershipId} for grain {GrainId}")]
    private static partial void LogOrphanedJobReclaimed(ILogger logger, string ownershipId, GrainId grainId);

    private sealed class PumpTimerState(
        DurableOutbox owner,
        DurableMessagingPumpExecution execution,
        DurableMessagingPumpLease lease,
        DurableJob job,
        CancellationToken jobCancellation)
    {
        public OneShotTimerHandle Handle { get; } = new();

        public async Task RunAsync(CancellationToken timerCancellation)
        {
            try
            {
                await owner.RunPumpTimerAsync(
                    execution,
                    lease,
                    job,
                    jobCancellation,
                    timerCancellation);
            }
            finally
            {
                owner._pumpCoordinator.Release(lease);
                owner._activePumpTurns--;
                Handle.Complete();
            }
        }
    }

    private readonly record struct PumpOwner(string? Id, DurableJob? Job, long Generation);
    private sealed record PendingMessage(DurableEnvelope Envelope, OutboxMessageState State);
    private sealed record OwnershipProposal(string Id, long Sequence, DurableJob Job);
    private sealed record DeliveryCandidate(DurableEnvelope Envelope, OutboxMessageState? State);
    private sealed record DeliveryOutcome(DeliveryCandidate Candidate, DeliveryResult? Result, string? Error, TimeSpan Duration, bool Expired);
    private sealed record PreparedDelivery(DeliveryOutcome Outcome, bool Remove, OutboxMessageState? Retry);

    private abstract class OutboxWrite(long generation)
    {
        public long Generation { get; } = generation;
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class DeliveryWrite(PumpOwner owner, DeliveryOutcome[] outcomes) : OutboxWrite(owner.Generation)
    {
        public PumpOwner Owner { get; } = owner;
        public DeliveryOutcome[] Outcomes { get; } = outcomes;
    }
    private sealed class ClearOwnerWrite(PumpOwner owner) : OutboxWrite(owner.Generation)
    {
        public PumpOwner Owner { get; } = owner;
    }
    private sealed class OwnershipWrite(long generation, bool replaceExisting) : OutboxWrite(generation)
    {
        public bool ReplaceExisting { get; } = replaceExisting;
    }
    private sealed class CompactWrite(long generation) : OutboxWrite(generation);

    private sealed class PendingDeliveryBatch(PumpOwner owner, CancellationTokenSource cancellation) : IDisposable
    {
        private bool _disposed;
        public PumpOwner Owner { get; } = owner;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public List<Task<DeliveryOutcome>> Attempts { get; } = [];
        public void Cancel()
        {
            if (_disposed)
            {
                return;
            }
            foreach (var attempt in Attempts)
            {
                _ = attempt.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            try
            {
                Cancellation.Cancel();
            }
            finally
            {
                Dispose();
            }
        }
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Cancellation.Dispose();
            }
        }
    }

    private sealed class EnsureJobTimerState(DurableOutbox owner)
    {
        public OneShotTimerHandle Handle { get; } = new();
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                await owner.EnsureJobScheduledAsync(false, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || owner._shutdown.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                LogPumpLoopError(owner._logger, exception);
            }
            finally
            {
                Volatile.Write(ref owner._ensureJobScheduledQueued, 0);
                Handle.Complete();
            }
        }
    }
}

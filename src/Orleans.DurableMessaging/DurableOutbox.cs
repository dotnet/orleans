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
/// Stages outbound messages using owner-bound durable collections.
/// The sequence state associates message cohorts with journal acknowledgement.
/// </summary>
internal sealed partial class DurableOutbox : IDurableOutbox, IDurableJobFeatureHandler, ILifecycleObserver, IJournaledStateCaptureHook
{
    internal const string JobName = "orleans.messaging.outbox-flush";
    public bool CanHandle(string jobName) => string.Equals(jobName, JobName, StringComparison.Ordinal);

    private readonly IJournaledStateManager _stateManager;
    private readonly IDurableDictionary<HierarchicalKey, DurableEnvelope> _messages;
    private readonly IGrainFactory _grainFactory;
    private readonly IGrainContext _grainContext;
    private readonly string _grainType;
    private readonly ITimerRegistry _timerRegistry;
    private readonly ILogger<DurableOutbox> _logger;
    private readonly DurableMessagingInstruments _instruments;
    private readonly TimeSpan _backpressureRetryDelay;
    private readonly TimeSpan _maxRetryAge;
    private readonly int _maxDeliveryAttempts;
    private readonly int _batchSize;
    private readonly TimeSpan _idleRetirementGracePeriod;
    private DateTimeOffset? _idleRetirementAt;
    private PumpOwner? _pendingRetirementOwner;
    private string? _previousCompletedOwner;
    private readonly TimeSpan _deadLetterRetentionPeriod;
    private readonly int _maxRetainedDeadLetters;
    private readonly IDurableDictionary<HierarchicalKey, OutboxMessageState> _messageStates;
    private readonly IDurableDictionary<HierarchicalKey, OutboxDeadLetter> _deadLetters;
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
    private readonly Dictionary<HierarchicalKey, PendingMessage> _pendingMessages = [];
    private int _unacknowledgedMessageCount;
    private PendingMessage[] _capturedMessages = [];
    private bool _captureStarted;
    private OwnershipProposal? _preparedOwnership;
    private string? _preparingOwnershipId;
    private Task? _ownershipPreparation;
    private int _activeOwnershipPreparations;
    private TaskCompletionSource? _ownershipPreparationsDrained;
    private int _activeWrites;
    private TaskCompletionSource? _writesDrained;
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
    private bool _localPumpRequested;
    private PumpTimerState? _pumpTimer;
    private LocalPumpTimerState? _localPumpTimer;
    private EnsureJobTimerState? _ensureJobTimer;
    private string _ownershipEpoch = Guid.NewGuid().ToString("N");
    private long _reservedSequence;
    private long _stateGeneration;
    private string? _ownershipStateError;
    private ExceptionDispatchInfo? _failure;
    private int _metricsActive;
    private int _reportedDepth;

    public DurableOutbox(
        IJournaledStateManager manager,
        IGrainFactory grainFactory,
        IGrainContext grainContext,
        ITimerRegistry timerRegistry,
        ILogger<DurableOutbox> logger,
        DurableMessagingInstruments instruments,
        ILocalDurableJobManager jobManager,
        IDurableJobHandlerRegistry jobHandlers,
        DurableMessagingPumpResults pumpResults,
        [FromKeyedServices(DurableJobTimeProviderNames.DurableJobs)] TimeProvider jobTimeProvider,
        IOptions<DurableInboxOptions> options,
        [FromKeyedServices(DurableMessagingStateNames.Outbox)] IDurableDictionary<HierarchicalKey, DurableEnvelope> messages,
        [FromKeyedServices(DurableMessagingStateNames.OutboxMessageState)] IDurableDictionary<HierarchicalKey, OutboxMessageState> messageStates,
        [FromKeyedServices(DurableMessagingStateNames.OutboxDeadLetters)] IDurableDictionary<HierarchicalKey, OutboxDeadLetter> deadLetters,
        [FromKeyedServices(DurableMessagingStateNames.OutboxJobId)] IDurableValue<string> jobId,
        [FromKeyedServices(DurableMessagingStateNames.OutboxJobHandle)] IDurableValue<DurableJob> job,
        [FromKeyedServices(DurableMessagingStateNames.OutboxCompletedJobId)] IDurableValue<string> completedJobId,
        IDurableValueCommandCodec<long> jobSequenceCodec)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(grainFactory);
        ArgumentNullException.ThrowIfNull(grainContext);
        ArgumentNullException.ThrowIfNull(timerRegistry);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(jobManager);
        ArgumentNullException.ThrowIfNull(jobHandlers);
        ArgumentNullException.ThrowIfNull(pumpResults);
        ArgumentNullException.ThrowIfNull(jobTimeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(messageStates);
        ArgumentNullException.ThrowIfNull(deadLetters);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(completedJobId);
        ArgumentNullException.ThrowIfNull(jobSequenceCodec);
        _stateManager = manager;
        _grainFactory = grainFactory;
        _grainContext = grainContext;
        _grainType = grainContext.GrainId.Type.ToString();
        _timerRegistry = timerRegistry;
        _logger = logger;
        _instruments = instruments;
        _jobManager = jobManager;
        _pumpResults = pumpResults;
        _jobTimeProvider = jobTimeProvider;
        _backpressureRetryDelay = options.Value.BackpressureRetryDelay;
        _maxRetryAge = options.Value.MaxOutboxRetryAge;
        _maxDeliveryAttempts = options.Value.MaxDeliveryAttempts;
        _batchSize = options.Value.OutboxBatchSize;
        _idleRetirementGracePeriod = options.Value.OutboxIdleRetirementGracePeriod;
        _deadLetterRetentionPeriod = options.Value.DeadLetterRetentionPeriod;
        _maxRetainedDeadLetters = options.Value.MaxRetainedDeadLetters;
        _messages = messages;
        _messageStates = messageStates;
        _deadLetters = deadLetters;
        _jobId = jobId;
        _job = job;
        _completedJobId = completedJobId;
        var sequence = new SequenceState(this, jobSequenceCodec);
        _jobSequence = sequence;
        manager.RegisterStateMachine(DurableMessagingStateNames.OutboxJobSequence, sequence);
        jobHandlers.Register(this);
        manager.Hooks.Add(this);

        var lifecycle = grainContext.ObservableLifecycle;
        lifecycle.Subscribe(RuntimeTypeNameFormatter.Format(GetType()), GrainLifecycleStage.Activate, this);
    }

    internal IDurableDictionary<HierarchicalKey, DurableEnvelope> MessageState => _messages;
    internal IDurableDictionary<HierarchicalKey, OutboxMessageState> AttemptState => _messageStates;
    internal IDurableDictionary<HierarchicalKey, OutboxDeadLetter> DeadLetterState => _deadLetters;
    internal IDurableValue<string> JobIdState => _jobId;
    internal IDurableValue<DurableJob> JobState => _job;
    internal IDurableValue<string> CompletedJobIdState => _completedJobId;
    internal IDurableValue<long> JobSequenceState => _jobSequence;

    public GrainId SenderId => _grainContext.GrainId;

    public int Count => _messages.Count;

    public IEnumerable<DurableEnvelope> Messages => _messages.Values;

    public bool TryGetMessage(HierarchicalKey messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope) =>
        _messages.TryGetValue(messageId, out envelope);

    private async ValueTask PrepareOwnershipAsync(bool replaceExisting, CancellationToken cancellationToken = default)
    {
        if (HasPreparedOwnership() || HasCommittedOwnership() && !replaceExisting)
        {
            return;
        }
        var preparation = _ownershipPreparation ??= ScheduleOwnershipAsync(cancellationToken);
        try
        {
            await preparation.ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(_ownershipPreparation, preparation))
            {
                _ownershipPreparation = null;
            }
        }
    }

    private async Task ScheduleOwnershipAsync(CancellationToken cancellationToken)
    {
        using var cancellation = DurableMessagingCancellation.Combine(cancellationToken, _shutdown.Token, out var combinedToken);
        var previousOwner = CurrentOwner;
        var sequence = checked(++_reservedSequence);
        var id = DurableMessagingJobOwnership.CreateId(_ownershipEpoch, sequence);
        _preparingOwnershipId = id;
        try
        {
            var job = await _jobManager.ScheduleJobAsync(new ScheduleJobRequest
            {
                Target = _grainContext.GrainId,
                JobName = JobName,
                DueTime = _jobTimeProvider.GetUtcNow(),
                Metadata = DurableMessagingJobOwnership.CreateMetadata(id)
            }, combinedToken).ConfigureAwait(true);
            ValidateOwner(previousOwner);
            _preparedOwnership = new(id, sequence, job, previousOwner);
        }
        catch
        {
            _failure?.Throw();
            throw;
        }
        finally
        {
            _preparingOwnershipId = null;
        }
    }

    async ValueTask IJournaledStateHook.BeforeOperationAsync(JournaledStateOperation operation, CancellationToken cancellationToken)
    {
        if (operation == JournaledStateOperation.Delete || _unacknowledgedMessageCount == 0 || HasCommittedOwnership())
        {
            return;
        }

        ValidateReady();
        // Owned writes can retain _gate through ACK. Share scheduling, not that gate, with the capture hook.
        StartOwnershipPreparation();
        try
        {
            await PrepareOwnershipAsync(replaceExisting: false, cancellationToken).ConfigureAwait(true);
            ValidateReady();
            ApplyPreparedOwnership();
        }
        finally
        {
            CompleteOwnershipPreparation();
        }
    }

    public void Send(DurableEnvelope envelope)
    {
        ValidateReady();
        ValidateEnvelope(envelope, nameof(envelope));
        if (_pendingMessages.TryGetValue(envelope.MessageId, out var pending))
        {
            ValidateEquivalent(pending.Envelope, envelope);
            return;
        }
        else if (_messages.TryGetValue(envelope.MessageId, out var existing))
        {
            ValidateEquivalent(existing, envelope);
            return;
        }
        else
        {
            pending = new(envelope);
            _pendingMessages.Add(envelope.MessageId, pending);
        }

        try
        {
            StageMessage(pending);
        }
        catch (Exception exception)
        {
            FailStaging(exception);
        }
    }

    private void ValidateEnvelope(DurableEnvelope envelope, string parameterName)
    {
        if (envelope.SenderId != _grainContext.GrainId)
        {
            throw new InvalidOperationException(
                $"Durable outbox sender '{envelope.SenderId}' does not match the owning grain '{_grainContext.GrainId}'.");
        }
        DurableEnvelopeValidation.Validate(envelope);
        if (envelope.ReceiverId.IsDefault)
        {
            throw new ArgumentException("The envelope receiver must not be the default grain ID.", parameterName);
        }
    }

    private void StageMessage(PendingMessage pending)
    {
        RestorePendingRetirement();
        var state = new OutboxMessageState { EnqueuedAt = _jobTimeProvider.GetUtcNow() };
        _idleRetirementAt = null;
        _messages.Add(pending.Envelope.MessageId, pending.Envelope);
        _messageStates.Add(pending.Envelope.MessageId, state);
        _unacknowledgedMessageCount++;
        EnsureMetricsActive();
        ReconcileOutboxDepth();
        _instruments.OnOutboxMessageSent(_grainType);
    }

    private static void ValidateEquivalent(DurableEnvelope existing, DurableEnvelope envelope)
    {
        if (!DurableEnvelopeEquivalence.AreEquivalent(existing, envelope))
        {
            throw new InvalidOperationException(
                $"The durable outbox already contains a different envelope with message ID '{envelope.MessageId}'.");
        }
    }

    private void StartOwnershipPreparation()
    {
        _activeOwnershipPreparations++;
    }

    private void CompleteOwnershipPreparation()
    {
        if (--_activeOwnershipPreparations == 0)
        {
            var waiter = _ownershipPreparationsDrained;
            _ownershipPreparationsDrained = null;
            waiter?.TrySetResult();
        }
        ReleaseUnusedOwnership();
    }

    private void ReleaseUnusedOwnership()
    {
        if (_activeOwnershipPreparations == 0 && _unacknowledgedMessageCount == 0 && !_captureStarted && _gate.CurrentCount != 0)
        {
            _preparedOwnership = null;
        }
    }

    private bool HasPreparedOwnership() => _preparedOwnership is { } proposal
        && proposal.PreviousOwner.Generation == _stateGeneration
        && ((string.Equals(proposal.PreviousOwner.Id, _jobId.Value, StringComparison.Ordinal)
            && (proposal.PreviousOwner.Job is null && _job.Value is null
                || DurableMessagingJobOwnership.IsSamePhysicalJob(proposal.PreviousOwner.Job, _job.Value)))
            || (string.Equals(proposal.Id, _jobId.Value, StringComparison.Ordinal)
                && DurableMessagingJobOwnership.IsSamePhysicalJob(proposal.Job, _job.Value)));

    private void ApplyPreparedOwnership()
    {
        var owner = _preparedOwnership!;
        if (string.Equals(owner.Id, _jobId.Value, StringComparison.Ordinal)
            && DurableMessagingJobOwnership.IsSamePhysicalJob(owner.Job, _job.Value))
        {
            return;
        }
        ValidateOwner(owner.PreviousOwner);
        _jobId.Value = owner.Id;
        _job.Value = owner.Job;
        _jobSequence.Value = owner.Sequence;
    }

    private void StageWrite(OutboxWrite operation)
    {
        ValidateGeneration(operation.Generation);
        Items<PreparedDelivery> deliveries = default;
        Dictionary<HierarchicalKey, OutboxDeadLetter>? deadLetters = null;
        switch (operation)
        {
            case DeliveryWrite delivery:
                ValidateOwner(delivery.Owner);
                for (var index = 0; index < delivery.Outcomes.Count; index++)
                {
                    ValidateCandidate(delivery.Outcomes[index].Candidate);
                }
                if (delivery.Outcomes.Count == 1)
                {
                    deliveries = new(PrepareDelivery(delivery.Outcomes[0], ref deadLetters));
                }
                else
                {
                    var prepared = new PreparedDelivery[delivery.Outcomes.Count];
                    for (var index = 0; index < prepared.Length; index++)
                    {
                        prepared[index] = PrepareDelivery(delivery.Outcomes[index], ref deadLetters);
                    }
                    deliveries = new(prepared);
                }
                delivery.Prepared = deliveries;
                break;
            case ClearOwnerWrite clear:
                ValidateOwner(clear.Owner);
                break;
            case OwnershipWrite:
                if (!HasPreparedOwnership())
                {
                    throw new InvalidOperationException("Outbox ownership must be prepared before staging.");
                }
                break;
            case CompactWrite:
                deadLetters = _deadLetters.ToDictionary(static pair => pair.Key, static pair => pair.Value);
                DurableDeadLetterRetention.Compact(deadLetters, _jobTimeProvider.GetUtcNow(),
                    _deadLetterRetentionPeriod, _maxRetainedDeadLetters, static entry => entry.DeadLetteredAt);
                break;
        }
        if (operation is OwnershipWrite)
        {
            ApplyPreparedOwnership();
        }
        for (var index = 0; index < deliveries.Count; index++)
        {
            var result = deliveries[index];
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
        if (deadLetters is not null)
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
        if (Count == 0)
        {
            var idleAt = GetIdleRetirementAt();
            if (CanRetireOwner() && _jobTimeProvider.GetUtcNow() >= idleAt)
            {
                if (operation is ClearOwnerWrite completed)
                {
                    ClearOwner(completed.Owner);
                }
                else if (operation is DeliveryWrite { ClearOwnershipWhenEmpty: true } delivered)
                {
                    ClearOwner(delivered.Owner);
                }
            }
        }
        ReconcileOutboxDepth();
    }

    private DateTimeOffset GetIdleRetirementAt() => _idleRetirementAt ??=
        DurableMessagingTime.AddClamped(_jobTimeProvider.GetUtcNow(), _idleRetirementGracePeriod);

    private bool CanRetireOwner() => Count == 0 && _activeOwnershipPreparations == 0
        && _unacknowledgedMessageCount == 0 && !_captureStarted && _activeWrites == 0;

    private void ClearOwner(PumpOwner owner)
    {
        // Admission and staging run synchronously. Hooks/capture may subsequently admit a new
        // cohort, which must prepare a new durable wakeup before its own acknowledgement.
        ValidateOwner(owner);
        _pendingRetirementOwner = owner;
        _previousCompletedOwner = _completedJobId.Value;
        _completedJobId.Value = owner.Id;
        _jobId.Value = null;
        _job.Value = null;
        _idleRetirementAt = null;
    }

    private void RestorePendingRetirement()
    {
        if (_pendingRetirementOwner is not { } owner || _captureStarted)
        {
            return;
        }
        // A hook may admit work after staging clear but before capture. Reuse only the
        // still-acknowledged physical handle, never overwrite an independently changed owner.
        if (owner.Generation == _stateGeneration && _jobId.Value is null && _job.Value is null
            && string.Equals(_durableOwnershipId, owner.Id, StringComparison.Ordinal)
            && DurableMessagingJobOwnership.IsSamePhysicalJob(_durableJob, owner.Job)
            && string.Equals(_completedJobId.Value, owner.Id, StringComparison.Ordinal))
        {
            _jobId.Value = owner.Id;
            _job.Value = owner.Job;
            _completedJobId.Value = _previousCompletedOwner;
        }
        _pendingRetirementOwner = null;
        _previousCompletedOwner = null;
    }

    private void CaptureWrite()
    {
        if (_captureStarted)
        {
            return;
        }
        var messages = _unacknowledgedMessageCount == 0 ? []
            : _pendingMessages.Values.Where(static entry => !entry.Captured).ToArray();
        var ownerChanged = !string.Equals(_durableOwnershipId, _jobId.Value, StringComparison.Ordinal)
            || !(_durableJob is null && _job.Value is null || DurableMessagingJobOwnership.IsSamePhysicalJob(_durableJob, _job.Value))
            || !string.Equals(_durableCompletedJobId, _completedJobId.Value, StringComparison.Ordinal);
        if (messages.Length == 0 && !ownerChanged)
        {
            return;
        }
        _captureStarted = true;
        _pendingRetirementOwner = null;
        _previousCompletedOwner = null;
        _capturedMessages = messages;
        foreach (var message in messages)
        {
            message.Captured = true;
        }
        _committingOwnershipId = _jobId.Value;
        _committingJob = _job.Value;
        _committingCompletedJobId = _completedJobId.Value;
    }

    private void CompleteCapture()
    {
        if (!_captureStarted)
        {
            return;
        }
        _durableOwnershipId = _committingOwnershipId;
        _durableJob = _committingJob;
        _durableCompletedJobId = _committingCompletedJobId;
        foreach (var message in _capturedMessages)
        {
            _unacknowledgedMessageCount--;
            _pendingMessages.Remove(message.Envelope.MessageId);
        }
        _capturedMessages = [];
        _captureStarted = false;
        ReleaseUnusedOwnership();
        ScheduleLocalPump();
    }

    internal void FailPersistence(Exception exception)
    {
        if (_failure is not null)
        {
            return;
        }
        _failure = ExceptionDispatchInfo.Capture(exception);
        Stop();
    }

    [DoesNotReturn]
    private void FailStaging(Exception exception)
    {
        FailPersistence(exception);
        LogStagingFailed(_logger, _failure!.SourceException);
        try
        {
            _grainContext.Deactivate(new DeactivationReason(
                DeactivationReasonCode.ApplicationError, _failure.SourceException, "Durable outbox staging failed."), CancellationToken.None);
        }
        catch (Exception deactivationException)
        {
            LogDeactivationFailed(_logger, deactivationException);
        }
        _failure.Throw();
    }

    private void OnRecoveryCompleted()
    {
        _reservedSequence = _jobSequence.Value;
        _durableOwnershipId = _jobId.Value;
        _durableJob = _job.Value;
        _durableCompletedJobId = _completedJobId.Value;
        _ownershipStateError = DurableMessagingJobOwnership.GetPairError(_jobId.Value, _job.Value);
        _recoveryCompleted = true;
        ReconcileOutboxDepth();
    }

    private void ResetState()
    {
        _recoveryCompleted = false;
        _pumpCoordinator.Reset();
        _pumpResults.Clear(JobName);
        _localPumpRequested = false;
        DisposePumpTimers();
        _stateGeneration++;
        _ownershipEpoch = Guid.NewGuid().ToString("N");
        _reservedSequence = 0;
        _idleRetirementAt = null;
        _pendingRetirementOwner = null;
        _previousCompletedOwner = null;
        _pendingMessages.Clear();
        _unacknowledgedMessageCount = 0;
        _preparedOwnership = null;
        _capturedMessages = [];
        _captureStarted = false;
        _durableOwnershipId = null;
        _durableJob = null;
        _durableCompletedJobId = null;
        _ownershipStateError = null;
    }

    private async ValueTask SubmitAsync(OutboxWrite operation)
    {
        ValidateReady();
        try
        {
            StageWrite(operation);
        }
        catch (Exception exception)
        {
            FailStaging(exception);
        }
        _activeWrites++;
        try
        {
            await _stateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);
            _failure?.Throw();
            if (operation is DeliveryWrite delivery)
            {
                var delivered = 0;
                var backpressured = 0;
                for (var index = 0; index < delivery.Prepared.Count; index++)
                {
                    var status = delivery.Prepared[index].Outcome.Result?.Status;
                    if (status is DeliveryStatus.Accepted or DeliveryStatus.Duplicate or DeliveryStatus.DeadLettered)
                    {
                        delivered++;
                    }
                    else if (status == DeliveryStatus.Backpressured)
                    {
                        backpressured++;
                    }
                }
                LogDeliveryComplete(_logger, delivered, backpressured, delivery.Prepared.Count - delivered - backpressured, Count);
            }
        }
        catch (JournaledStatePreCommitException exception)
        {
            // This owned operation has already staged its effects. Retire it and replay a fresh owner.
            FailPersistence(exception);
            LogPrerequisiteFailed(_logger, exception);
            try
            {
                _grainContext.Deactivate(new DeactivationReason(
                    DeactivationReasonCode.ApplicationError, exception, "Durable outbox persistence prerequisite failed."), CancellationToken.None);
            }
            catch (Exception deactivationException)
            {
                LogDeactivationFailed(_logger, deactivationException);
            }
            throw;
        }
        catch (JournaledStatePostCommitException)
        {
            throw;
        }
        catch (Exception exception)
        {
            FailPersistence(exception);
            _failure!.Throw();
            throw;
        }
        finally
        {
            if (--_activeWrites == 0)
            {
                var waiter = _writesDrained;
                _writesDrained = null;
                waiter?.TrySetResult();
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
        || (_preparedOwnership is { } proposal && string.Equals(ownershipId, proposal.Id, StringComparison.Ordinal)
            && (!HasCommittedOwnership() || !DurableMessagingJobOwnership.IsSamePhysicalJob(proposal.Job, _job.Value)))
        || !string.Equals(_durableOwnershipId, _jobId.Value, StringComparison.Ordinal)
        || (_job.Value is not null && !DurableMessagingJobOwnership.IsSamePhysicalJob(_durableJob, _job.Value));

    private bool IsPendingAcknowledgement(HierarchicalKey id) =>
        _pendingMessages.ContainsKey(id);

    private Items<DeliveryCandidate> SelectMessages()
    {
        var now = _jobTimeProvider.GetUtcNow();
        DeliveryCandidate first = default;
        List<DeliveryCandidate>? candidates = null;
        var count = 0;
        foreach (var envelope in _messages.Values)
        {
            if (!IsPendingAcknowledgement(envelope.MessageId) && IsReadyForAttempt(envelope, now))
            {
                var candidate = new DeliveryCandidate(envelope.Retain(),
                    _messageStates.TryGetValue(envelope.MessageId, out var state) ? CopyState(state) : null);
                if (count++ == 0)
                {
                    first = candidate;
                }
                else
                {
                    (candidates ??= [first]).Add(candidate);
                }
                if (count == _batchSize)
                {
                    break;
                }
            }
        }
        if (count == 0)
        {
            LogNoDurableMessages(_logger, Count);
            return default;
        }
        LogDeliveringMessages(_logger, count);
        return candidates is null ? new(first) : new(candidates.ToArray());
    }

    private static void ReleaseCandidates(Items<DeliveryCandidate> candidates)
    {
        for (var index = 0; index < candidates.Count; index++) candidates[index].Envelope.Dispose();
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
        if (IsPendingAcknowledgement(candidate.Envelope.MessageId)
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
        var start = Stopwatch.GetTimestamp();
        try
        {
            var envelope = candidate.Envelope;
            var target = envelope.ReceiverId == _grainContext.GrainId
                ? _grainContext.GetGrainExtension<IDurableInboxExtension>()
                : _grainFactory.GetGrain<IDurableInboxExtension>(envelope.ReceiverId);
            var result = await target.DeliverAsync(envelope, cancellationToken).ConfigureAwait(true);
            return new(candidate, result, null, Stopwatch.GetElapsedTime(start), Expired: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogDeliveryError(_logger, exception, candidate.Envelope.MessageId, candidate.Envelope.SenderId,
                candidate.Envelope.ReceiverId);
            return new(candidate, null, exception.ToString(), Stopwatch.GetElapsedTime(start), Expired: false);
        }
    }

    public async Task DeliverPendingMessagesAsync(CancellationToken cancellationToken = default)
    {
        await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        Items<DeliveryCandidate> candidates = default;
        try
        {
            ValidateReady();
            var owner = CurrentOwner;
            candidates = SelectMessages();
            Items<DeliveryOutcome> outcomes = default;
            if (candidates.Count == 1)
            {
                outcomes = new(await DeliverAsync(candidates[0], cancellationToken).ConfigureAwait(true));
            }
            else if (candidates.Count > 1)
            {
                var results = new DeliveryOutcome[candidates.Count];
                for (var index = 0; index < candidates.Count; index++)
                {
                    results[index] = await DeliverAsync(candidates[index], cancellationToken).ConfigureAwait(true);
                }
                outcomes = new(results);
            }
            cancellationToken.ThrowIfCancellationRequested();
            ValidateOwner(owner);
            if (outcomes.Count > 0)
            {
                await SubmitAsync(new DeliveryWrite(owner, outcomes)).ConfigureAwait(true);
            }
        }
        finally
        {
            ReleaseCandidates(candidates);
            _deliveryGate.Release();
        }
    }

    private async Task AdvancePendingDeliveriesAsync(DurableJob job, long generation,
        CancellationToken cancellationToken, CancellationToken attemptCancellationToken, bool clearOwnershipWhenEmpty)
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
                    CancelPendingDeliveryBatchAsync().Ignore();
                    return;
                }
                if (!batch.Completion.IsCompleted)
                {
                    return;
                }
                _pendingDeliveryBatch = null;
                using (batch)
                {
                    var outcomes = await batch.GetOutcomesAsync().ConfigureAwait(true);
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateOwner(batch.Owner);
                    await SubmitAsync(new DeliveryWrite(batch.Owner, outcomes, clearOwnershipWhenEmpty)).ConfigureAwait(true);
                }
                return;
            }

            var candidates = SelectMessages();
            if (candidates.Count == 0)
            {
                return;
            }
            var owner = new PumpOwner(_jobId.Value, job, generation);
            // This source owns explicit batch cancellation as well as activation/attempt cancellation.
            var cancellation = attemptCancellationToken == _shutdown.Token || !attemptCancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(attemptCancellationToken, _shutdown.Token);
            var pending = new PendingDeliveryBatch(owner, cancellation, candidates);
            try
            {
                for (var index = 0; index < candidates.Count; index++)
                {
                    var candidate = candidates[index];
                    if (candidate.Envelope.ReceiverId == _grainContext.GrainId)
                    {
                        var attempt = DeliverAsync(candidate, cancellationToken);
                        pending.Add(attempt);
                        await attempt.ConfigureAwait(true);
                        ValidateReady();
                        if (!IsCurrentPump(job, generation))
                        {
                            await pending.CancelAsync(_logger).ConfigureAwait(true);
                            return;
                        }
                    }
                    else
                    {
                        pending.Add(DeliverAsync(candidate, cancellation.Token));
                    }
                }
                pending.Seal();
                if (pending.Completion.IsCompleted)
                {
                    using (pending)
                    {
                        var outcomes = await pending.GetOutcomesAsync().ConfigureAwait(true);
                        cancellationToken.ThrowIfCancellationRequested();
                        ValidateOwner(owner);
                        await SubmitAsync(new DeliveryWrite(owner, outcomes, clearOwnershipWhenEmpty)).ConfigureAwait(true);
                    }
                }
                else
                {
                    _pendingDeliveryBatch = pending;
                    WakeOnDeliveryCompletionAsync(pending).Ignore();
                }
            }
            catch
            {
                await pending.CancelAsync(_logger).ConfigureAwait(true);
                throw;
            }
        }
        finally
        {
            _deliveryGate.Release();
        }
    }

    private async Task WakeOnDeliveryCompletionAsync(PendingDeliveryBatch batch)
    {
        // Completion wakes the pump; the retained batch remains authoritative for success, cancellation, and failure.
        await ((Task)batch.Completion).ConfigureAwait(
            ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        if (ReferenceEquals(_pendingDeliveryBatch, batch) && IsCurrentPump(batch.Owner.Job!, batch.Owner.Generation))
        {
            ScheduleLocalPump();
        }
    }

    private PreparedDelivery PrepareDelivery(DeliveryOutcome outcome, ref Dictionary<HierarchicalKey, OutboxDeadLetter>? deadLetters)
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
            DeliveryStatus.HandlerNotFound => outcome.Result?.Message ?? "The receiver has no registered inbox handler.",
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
            deadLetters ??= _deadLetters.ToDictionary(static pair => pair.Key, static pair => pair.Value);
            DurableDeadLetterRetention.Compact(deadLetters, now, _deadLetterRetentionPeriod,
                _maxRetainedDeadLetters, static entry => entry.DeadLetteredAt,
                reservedCapacity: deadLetters.ContainsKey(letter.Envelope.MessageId) ? 0 : 1);
            deadLetters[letter.Envelope.MessageId] = letter;
            return new(outcome, true, null);
        }
        var exponent = Math.Min(state.AttemptCount - 1, DurableInboxOptions.MaximumBackoffExponent);
        state.NextAttemptAt = DurableMessagingTime.AddClamped(now, TimeSpan.FromTicks(_backpressureRetryDelay.Ticks * (1L << exponent)));
        return new(outcome, false, state);
    }

    private void RecordDeliveryMetrics(PreparedDelivery delivery)
    {
        var outcome = delivery.Outcome;
        var envelope = outcome.Candidate.Envelope;
        var status = outcome.Result?.Status;
        var metricStatus = status switch
        {
            DeliveryStatus.Accepted => "accepted",
            DeliveryStatus.Duplicate => "duplicate",
            DeliveryStatus.DeadLettered => "deadlettered",
            DeliveryStatus.Backpressured => "backpressured",
            DeliveryStatus.HandlerNotFound => "handler_not_found",
            null => "error",
            _ => status.Value.ToString().ToLowerInvariant()
        };
        _instruments.OnOutboxMessageDelivered(_grainType, metricStatus);
        _instruments.OnOutboxDeliveryDuration(outcome.Duration, _grainType);
        switch (status)
        {
            case DeliveryStatus.Accepted:
            case DeliveryStatus.Duplicate:
            case DeliveryStatus.DeadLettered:
                LogMessageDelivered(_logger, envelope.MessageId, envelope.SenderId, envelope.ReceiverId, status.Value);
                break;
            case DeliveryStatus.Backpressured:
                LogDeliveryBackpressured(_logger, envelope.MessageId, envelope.ReceiverId);
                break;
            case DeliveryStatus.HandlerNotFound:
                LogDeliveryHandlerNotFound(_logger, envelope.MessageId, envelope.SenderId, envelope.ReceiverId, outcome.Result?.Message);
                break;
            case { } unexpected:
                LogUnexpectedDeliveryStatus(_logger, unexpected, envelope.MessageId, envelope.SenderId, envelope.ReceiverId);
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

    private async Task CancelPendingDeliveryBatchAsync()
    {
        if (_pendingDeliveryBatch is { } batch)
        {
            try
            {
                await batch.CancelAsync(_logger).ConfigureAwait(true);
            }
            finally
            {
                if (ReferenceEquals(_pendingDeliveryBatch, batch))
                {
                    _pendingDeliveryBatch = null;
                }
            }
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
        ScheduleLocalPump();
        if (_deadLetters.Count > _maxRetainedDeadLetters || _deadLetters.Values.Any(entry =>
            DurableMessagingTime.IsExpired(_jobTimeProvider.GetUtcNow(), entry.DeadLetteredAt, _deadLetterRetentionPeriod)))
        {
            await SubmitAsync(new CompactWrite(_stateGeneration)).ConfigureAwait(true);
        }
    }

    public async Task OnStop(CancellationToken cancellationToken = default)
    {
        try
        {
            Stop();
        }
        finally
        {
            try
            {
                await _deliveryGate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
                try
                {
                    await CancelPendingDeliveryBatchAsync().ConfigureAwait(true);
                }
                finally
                {
                    _deliveryGate.Release();
                }
            }
            finally
            {
                try
                {
                    if (_activeOwnershipPreparations != 0)
                    {
                        await (_ownershipPreparationsDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.ConfigureAwait(true);
                    }
                }
                finally
                {
                    if (_activeWrites != 0)
                    {
                        await (_writesDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.ConfigureAwait(true);
                    }
                }
            }
        }
    }

    private void Stop()
    {
        _stateGeneration++;
        _preparedOwnership = null;
        _pumpCoordinator.Reset();
        _pumpResults.Clear(JobName);
        try
        {
            _shutdown.Cancel();
        }
        catch (AggregateException exception)
        {
            LogCancellationFailed(_logger, exception);
        }
        finally
        {
            DisposePumpTimers();
            CancelPendingDeliveryBatchAsync().Ignore();
            if (Interlocked.Exchange(ref _metricsActive, 0) != 0)
            {
                _instruments.OnOutboxDepthChanged(-Interlocked.Exchange(ref _reportedDepth, 0));
            }
        }
    }

    private void DisposePumpTimers()
    {
        _pumpTimer?.Dispose();
        _localPumpTimer?.Dispose();
        _ensureJobTimer?.Dispose();
        _pumpTimer = null;
        _localPumpTimer = null;
        _ensureJobTimer = null;
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
        try
        {
            (_ensureJobTimer ??= new(this)).Queue(new(_stateGeneration));
        }
        catch
        {
            Volatile.Write(ref _ensureJobScheduledQueued, 0);
            throw;
        }
    }

    private void ScheduleLocalPump()
    {
        if (_shutdown.IsCancellationRequested || _failure is not null || Count == 0 || !HasCommittedOwnership())
        {
            return;
        }

        _localPumpRequested = true;
        if (_pumpCoordinator.IsActive)
        {
            return;
        }

        var job = _job.Value!;
        if (!_pumpCoordinator.TryAcquire(_jobId.Value!, _shutdown.Token, out var lease))
        {
            return;
        }

        _localPumpRequested = false;
        _activePumpTurns++;
        try
        {
            (_localPumpTimer ??= new(this)).Queue(new(lease, job, _stateGeneration));
        }
        catch
        {
            _activePumpTurns--;
            _pumpCoordinator.Release(lease);
            throw;
        }
    }

    internal async Task EnsureJobScheduledAsync(bool replaceExisting, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        StartOwnershipPreparation();
        try
        {
            ValidateReady();
            if (_messages.Count > 0 && (replaceExisting || !HasCommittedOwnership()))
            {
                await PrepareOwnershipAsync(replaceExisting, CancellationToken.None).ConfigureAwait(true);
                await SubmitAsync(new OwnershipWrite(_stateGeneration)).ConfigureAwait(true);
            }
        }
        finally
        {
            _gate.Release();
            CompleteOwnershipPreparation();
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
        try
        {
            (_pumpTimer ??= new(this)).Queue(new(execution, lease, context.Job, cancellationToken));
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
            using var linkedCancellation = DurableMessagingCancellation.Combine(
                jobCancellation, timerCancellation, _shutdown.Token, out var combinedToken);
            result = await ExecuteJobCoreAsync(
                lease.OwnershipId,
                job,
                execution.Key.StateGeneration,
                combinedToken,
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
        CancellationToken cancellationToken, CancellationToken attemptCancellationToken, bool clearOwnershipWhenEmpty = true)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            ValidateReady();
            if (!IsCurrentPump(job, stateGeneration) || IsOwnershipTransitionPending(jobId))
            {
                return DurableMessagingJobOwnership.IsCompleted(_durableCompletedJobId, jobId)
                    ? DurableJobRunResult.Completed : DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
            }
        }
        finally
        {
            _gate.Release();
        }
        await AdvancePendingDeliveriesAsync(job, stateGeneration, cancellationToken, attemptCancellationToken, clearOwnershipWhenEmpty).ConfigureAwait(true);
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
                return DurableMessagingJobOwnership.IsCompleted(_durableCompletedJobId, jobId)
                    ? DurableJobRunResult.Completed : DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
            }
            if (Count == 0 && _activeOwnershipPreparations == 0)
            {
                if (!clearOwnershipWhenEmpty)
                {
                    return DurableJobRunResult.Completed;
                }
                var idleAt = GetIdleRetirementAt();
                if (_jobTimeProvider.GetUtcNow() < idleAt)
                {
                    return DurableJobRunResult.RescheduleAt(idleAt);
                }
                if (!CanRetireOwner())
                {
                    return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
                }
                await SubmitAsync(new ClearOwnerWrite(new(jobId, job, stateGeneration))).ConfigureAwait(true);
                if (DurableMessagingJobOwnership.IsCompleted(_durableCompletedJobId, jobId))
                {
                    return DurableJobRunResult.Completed;
                }
            }
            if (_unacknowledgedMessageCount > 0)
            {
                return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
            }
            var now = _jobTimeProvider.GetUtcNow();
            var next = Count == 0 ? now : DateTimeOffset.MaxValue;
            foreach (var envelope in _messages.Values)
            {
                if (GetNextAttemptAt(envelope, now) is not { } at || at <= now)
                {
                    next = now;
                    break;
                }
                if (at < next)
                {
                    next = at;
                }
            }
            return DurableJobRunResult.RescheduleAt(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Durable outbox persistence prerequisite failed; retiring the staged operation.")]
    private static partial void LogPrerequisiteFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Durable outbox command staging failed.")]
    private static partial void LogStagingFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Requesting deactivation after an outbox staging failure failed.")]
    private static partial void LogDeactivationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "An outbox cancellation callback failed during cleanup.")]
    private static partial void LogCancellationFailed(ILogger logger, Exception exception);

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
        Message = "Delivered message {MessageId} from {SenderId} to {ReceiverId} (Status: {Status})")]
    private static partial void LogMessageDelivered(ILogger logger, HierarchicalKey messageId, GrainId senderId, GrainId receiverId, DeliveryStatus status);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No inbox handler for message {MessageId} from {SenderId} to {ReceiverId}: {Message}")]
    private static partial void LogDeliveryHandlerNotFound(ILogger logger, HierarchicalKey messageId, GrainId senderId, GrainId receiverId, string? message);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Backpressured delivering message {MessageId} to {ReceiverId}, will retry later")]
    private static partial void LogDeliveryBackpressured(ILogger logger, HierarchicalKey messageId, GrainId receiverId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Unexpected delivery status {Status} for message {MessageId} from {SenderId} to {ReceiverId}")]
    private static partial void LogUnexpectedDeliveryStatus(ILogger logger, DeliveryStatus status, HierarchicalKey messageId, GrainId senderId, GrainId receiverId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error delivering message {MessageId} from {SenderId} to {ReceiverId}")]
    private static partial void LogDeliveryError(ILogger logger, Exception exception, HierarchicalKey messageId, GrainId senderId, GrainId receiverId);

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

    private readonly record struct PumpTurn(DurableMessagingPumpExecution Execution,
        DurableMessagingPumpLease Lease, DurableJob Job, CancellationToken JobCancellation);
    private readonly record struct LocalPumpTurn(DurableMessagingPumpLease Lease, DurableJob Job, long Generation);
    private readonly record struct EnsureJobTurn(long Generation);

    private sealed class PumpTimerState(DurableOutbox owner) : DurableMessagingTurn<PumpTurn>
    {
        protected override IGrainTimer RegisterTimer(long registrationGeneration) => owner._timerRegistry.RegisterGrainTimer(
            owner._grainContext, (state, token) => state.RunAsync(registrationGeneration, token), this,
            new GrainTimerCreationOptions(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan) { Interleave = false, KeepAlive = true });

        protected override async Task ExecuteAsync(PumpTurn turn, CancellationToken timerCancellation)
        {
            try
            {
                await owner.RunPumpTimerAsync(turn.Execution, turn.Lease, turn.Job, turn.JobCancellation, timerCancellation);
            }
            finally
            {
                Release(turn);
                if (owner._localPumpRequested) owner.ScheduleLocalPump();
            }
        }

        protected override void Discard(PumpTurn turn)
        {
            owner._pumpResults.Discard(turn.Execution);
            Release(turn);
        }

        private void Release(PumpTurn turn)
        {
            owner._pumpCoordinator.Release(turn.Lease);
            owner._activePumpTurns--;
        }
    }

    private sealed class LocalPumpTimerState(DurableOutbox owner) : DurableMessagingTurn<LocalPumpTurn>
    {
        protected override IGrainTimer RegisterTimer(long registrationGeneration) => owner._timerRegistry.RegisterGrainTimer(
            owner._grainContext, (state, token) => state.RunAsync(registrationGeneration, token), this,
            new GrainTimerCreationOptions(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan) { Interleave = false, KeepAlive = true });

        protected override async Task ExecuteAsync(LocalPumpTurn turn, CancellationToken timerCancellation)
        {
            try
            {
                if (owner._pumpCoordinator.IsCurrent(turn.Lease) && owner.IsCurrentPump(turn.Job, turn.Generation))
                {
                    using var cancellation = DurableMessagingCancellation.Combine(timerCancellation, owner._shutdown.Token, out var combinedToken);
                    _ = await owner.ExecuteJobCoreAsync(turn.Lease.OwnershipId, turn.Job, turn.Generation,
                        combinedToken, owner._shutdown.Token, clearOwnershipWhenEmpty: false);
                }
            }
            finally
            {
                Discard(turn);
                if (owner._localPumpRequested) owner.ScheduleLocalPump();
            }
        }

        protected override void Discard(LocalPumpTurn turn)
        {
            owner._pumpCoordinator.Release(turn.Lease);
            owner._activePumpTurns--;
        }
    }

    private readonly record struct PumpOwner(string? Id, DurableJob? Job, long Generation);
    private sealed class PendingMessage(DurableEnvelope envelope)
    {
        public DurableEnvelope Envelope { get; } = envelope;
        public bool Captured { get; set; }
    }

    private sealed record OwnershipProposal(string Id, long Sequence, DurableJob Job, PumpOwner PreviousOwner);
    private readonly record struct DeliveryCandidate(DurableEnvelope Envelope, OutboxMessageState? State);
    private readonly record struct DeliveryOutcome(DeliveryCandidate Candidate, DeliveryResult? Result, string? Error, TimeSpan Duration, bool Expired);
    private readonly record struct PreparedDelivery(DeliveryOutcome Outcome, bool Remove, OutboxMessageState? Retry);

    private abstract class OutboxWrite(long generation)
    {
        public long Generation { get; } = generation;
    }
    // The scalar form carries no collection; arrays are reserved for true fan-out.
    private readonly struct Items<T>
    {
        private readonly T _single;
        private readonly T[]? _many;
        public Items(T single) { _single = single; _many = null; Count = 1; }
        public Items(T[] many) { _single = default!; _many = many; Count = many.Length; }
        public int Count { get; }
        public T this[int index] => _many is null ? _single : _many[index];
    }

    private sealed class DeliveryWrite(PumpOwner owner, Items<DeliveryOutcome> outcomes, bool clearOwnershipWhenEmpty = false) : OutboxWrite(owner.Generation)
    {
        public PumpOwner Owner { get; } = owner;
        public Items<DeliveryOutcome> Outcomes { get; } = outcomes;
        public bool ClearOwnershipWhenEmpty { get; } = clearOwnershipWhenEmpty;
        public Items<PreparedDelivery> Prepared { get; set; }
    }
    private sealed class ClearOwnerWrite(PumpOwner owner) : OutboxWrite(owner.Generation)
    {
        public PumpOwner Owner { get; } = owner;
    }
    private sealed class OwnershipWrite(long generation) : OutboxWrite(generation);
    private sealed class CompactWrite(long generation) : OutboxWrite(generation);

    private sealed class PendingDeliveryBatch(PumpOwner owner, CancellationTokenSource cancellation, Items<DeliveryCandidate> candidates) : IDisposable
    {
        private bool _disposed;
        private Task? _drain;
        private bool _sealed;
        public PumpOwner Owner { get; } = owner;
        public Task Completion { get; private set; } = Task.CompletedTask;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<DeliveryOutcome>? SingleAttempt { get; private set; }
        public List<Task<DeliveryOutcome>>? Attempts { get; private set; }

        public void Add(Task<DeliveryOutcome> attempt)
        {
            if (SingleAttempt is null)
            {
                SingleAttempt = attempt;
            }
            else
            {
                (Attempts ??= [SingleAttempt]).Add(attempt);
            }
        }

        public void Seal()
        {
            if (!_sealed)
            {
                _sealed = true;
                Completion = Attempts is { } attempts ? Task.WhenAll(attempts)
                    : SingleAttempt ?? (Task)Task.CompletedTask;
            }
        }

        public async ValueTask<Items<DeliveryOutcome>> GetOutcomesAsync()
        {
            if (Attempts is null)
            {
                return new(await SingleAttempt!.ConfigureAwait(true));
            }
            return new(await ((Task<DeliveryOutcome[]>)Completion).ConfigureAwait(true));
        }
        public Task CancelAsync(ILogger<DurableOutbox> logger)
        {
            if (_drain is { } drain)
            {
                return drain;
            }
            if (_disposed)
            {
                return Task.CompletedTask;
            }
            // Seal partial fan-out too: an exception while admitting self-delivery must still drain remote attempts.
            if (!_sealed)
            {
                Seal();
            }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _drain = completion.Task;
            try
            {
                Cancellation.Cancel();
            }
            catch (AggregateException exception)
            {
                LogCancellationFailed(logger, exception);
            }
            DrainAsync(completion).Ignore();
            return _drain;
        }

        private async Task DrainAsync(TaskCompletionSource completion)
        {
            // Delivery already logs transport failures. Retirement observes every outcome before releasing the token source.
            await Completion.ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            Dispose();
            completion.TrySetResult();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                ReleaseCandidates(candidates);
                Cancellation.Dispose();
            }
        }
    }

    private sealed class EnsureJobTimerState(DurableOutbox owner) : DurableMessagingTurn<EnsureJobTurn>
    {
        protected override IGrainTimer RegisterTimer(long registrationGeneration) => owner._timerRegistry.RegisterGrainTimer(
            owner._grainContext, (state, token) => state.RunAsync(registrationGeneration, token), this,
            new GrainTimerCreationOptions(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan) { Interleave = false, KeepAlive = true });

        protected override async Task ExecuteAsync(EnsureJobTurn turn, CancellationToken cancellationToken)
        {
            try
            {
                if (turn.Generation == owner._stateGeneration)
                {
                    await owner.EnsureJobScheduledAsync(false, cancellationToken).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || owner._shutdown.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                LogPumpLoopError(owner._logger, exception);
                if (owner._failure is null && !owner._shutdown.IsCancellationRequested)
                {
                    owner._grainContext.Deactivate(new DeactivationReason(
                        DeactivationReasonCode.ApplicationError, exception, "Durable outbox ownership repair failed."), CancellationToken.None);
                }
            }
            finally
            {
                Discard(turn);
            }
        }

        protected override void Discard(EnsureJobTurn turn) => Volatile.Write(ref owner._ensureJobScheduledQueued, 0);
    }
}

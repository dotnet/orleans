using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;
using Orleans.Timers;

namespace Orleans.DurableMessaging;

/// <summary>
/// Implementation of durable inbox extension for grain message delivery.
/// Handles message persistence, deduplication, and processing.
/// </summary>
internal sealed partial class DurableInboxExtension :
    IDurableInboxExtension,
    IDurableJobFeatureHandler,
    IJournaledStateObserver,
    ILifecycleObserver,
    IDisposable
{
    internal const string JobName = "orleans.messaging.inbox-drain";

    public bool CanHandle(string jobName) => string.Equals(jobName, JobName, StringComparison.Ordinal);

    private readonly IGrainContext _grainContext;
    private readonly ITimerRegistry _timerRegistry;
    private readonly IJournaledStateManager _stateManager;
    private readonly SerializerSessionPool _sessionPool;
    private readonly ILogger<DurableInboxExtension> _logger;
    private readonly DurableMessagingInstruments _instruments;
    private readonly DurableInbox _durableInbox;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> _inboxDict;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> _processed;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), InboxMessageState> _messageStates;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), InboxDeadLetter> _deadLetters;
    private readonly IDurableValue<string> _jobId;
    private readonly IDurableValue<DurableJob> _job;
    private readonly IDurableValue<string> _completedJobId;
    private readonly IDurableValue<long> _jobSequence;
    private readonly IDurableOutbox _outbox;
    private readonly ILocalDurableJobManager _jobManager;
    private readonly TimeProvider _timeProvider;
    private readonly TimeProvider _jobTimeProvider;
    private readonly HashSet<(GrainId SenderId, Guid MessageId)> _provisionalAcceptances = [];
    private readonly DurableMessagingPumpResults _pumpResults;
    private readonly DurableMessagingPumpCoordinator _pumpCoordinator = new();
    private readonly int _maxCapacity;
    private readonly TimeSpan _deduplicationWindow;
    private readonly TimeSpan _processedCompactionInterval;
    private readonly int _maxProcessingAttempts;
    private readonly int _batchSize;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _deadLetterRetentionPeriod;
    private readonly int _maxRetainedDeadLetters;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _disposed;
    private int _handlerWriteRejected;
    private int _metricsActive;
    private int _reportedDepth;
    private int _handlerExecutionDepth;
    private string? _committingOwnershipId;
    private DurableJob? _committingJob;
    private string? _durableOwnershipId;
    private DurableJob? _durableJob;
    private string _ownershipEpoch = Guid.NewGuid().ToString("N");
    private long _stateGeneration;
    private bool _recoveryCompleted;
    private string? _ownershipStateError;
    private long _reservedSequence;
    private ExceptionDispatchInfo? _failure;
    private readonly HashSet<string> _pendingOwnershipIds = new(StringComparer.Ordinal);
    private readonly List<InboxWrite> _pendingWrites = [];
    private InboxWrite[] _admittedWrites = [];
    private string? _durableCompletedJobId;
    private string? _committingCompletedJobId;
    private DateTimeOffset? _nextProcessedExpiry;
    private DateTimeOffset? _lastProcessedCompaction;

    /// <summary>
    /// Creates a new inbox extension instance.
    /// </summary>
    /// <param name="grainContext">The grain context for this extension.</param>
    /// <param name="stateManager">State manager for atomic persistence.</param>
    /// <param name="sessionPool">Serializer session pool for envelope creation.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="instruments">Journaling metrics.</param>
    /// <param name="durableInbox">The grain's durable inbox (shared with grain DI).</param>
    /// <param name="inboxDict">Durable dictionary for inbox messages.</param>
    /// <param name="processed">Durable dictionary for processed message tracking.</param>
    /// <param name="outbox">Durable outbox for sending response messages.</param>
    /// <param name="options">Durable messaging options.</param>
    public DurableInboxExtension(
        IGrainContext grainContext,
        ITimerRegistry timerRegistry,
        IJournaledStateManager stateManager,
        SerializerSessionPool sessionPool,
        ILogger<DurableInboxExtension> logger,
        DurableMessagingInstruments instruments,
        DurableInbox durableInbox,
        IDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> inboxDict,
        IDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> processed,
        IDictionary<(GrainId SenderId, Guid MessageId), InboxMessageState> messageStates,
        IDictionary<(GrainId SenderId, Guid MessageId), InboxDeadLetter> deadLetters,
        IDurableValue<string> jobId,
        IDurableValue<DurableJob> job,
        IDurableValue<string> completedJobId,
        IDurableValue<long> jobSequence,
        IDurableOutbox outbox,
        ILocalDurableJobManager jobManager,
        IDurableJobHandlerRegistry jobHandlers,
        DurableMessagingPumpResults pumpResults,
        TimeProvider timeProvider,
        TimeProvider jobTimeProvider,
        DurableInboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(grainContext);
        ArgumentNullException.ThrowIfNull(timerRegistry);
        ArgumentNullException.ThrowIfNull(stateManager);
        ArgumentNullException.ThrowIfNull(sessionPool);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(durableInbox);
        ArgumentNullException.ThrowIfNull(inboxDict);
        ArgumentNullException.ThrowIfNull(processed);
        ArgumentNullException.ThrowIfNull(messageStates);
        ArgumentNullException.ThrowIfNull(deadLetters);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(completedJobId);
        ArgumentNullException.ThrowIfNull(jobSequence);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(jobManager);
        ArgumentNullException.ThrowIfNull(jobHandlers);
        ArgumentNullException.ThrowIfNull(pumpResults);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(jobTimeProvider);
        ArgumentNullException.ThrowIfNull(options);
        _grainContext = grainContext;
        _timerRegistry = timerRegistry;
        _stateManager = stateManager;
        _sessionPool = sessionPool;
        _logger = logger;
        _instruments = instruments;
        _durableInbox = durableInbox;
        _inboxDict = inboxDict;
        _processed = processed;
        _messageStates = messageStates;
        _deadLetters = deadLetters;
        _jobId = jobId;
        _job = job;
        _completedJobId = completedJobId;
        _jobSequence = jobSequence;
        _outbox = outbox;
        _jobManager = jobManager;
        _pumpResults = pumpResults;
        _timeProvider = timeProvider;
        _jobTimeProvider = jobTimeProvider;
        _maxCapacity = options.MaxCapacity;
        _deduplicationWindow = options.DeduplicationWindow;
        _processedCompactionInterval = TimeSpan.FromTicks(Math.Max(1, _deduplicationWindow.Ticks / 4));
        _maxProcessingAttempts = options.MaxProcessingAttempts;
        _batchSize = options.InboxBatchSize;
        _retryDelay = options.BackpressureRetryDelay;
        _deadLetterRetentionPeriod = options.DeadLetterRetentionPeriod;
        _maxRetainedDeadLetters = options.MaxRetainedDeadLetters;
        jobHandlers.Register(this);
        grainContext.ObservableLifecycle.Subscribe(
            RuntimeTypeNameFormatter.Format(GetType()),
            GrainLifecycleStage.Activate,
            this);
    }

    private bool TryFindHandlerWithinMutationBoundary(IInboxHandlerContext context, [MaybeNullWhen(false)] out IInboxHandler handler)
    {
        _handlerWriteRejected = 0;
        _handlerExecutionDepth++;
        try
        {
            var result = _durableInbox.TryFindHandler(context, out handler);
            ThrowIfHandlerWriteRejected();
            return result;
        }
        finally
        {
            _handlerExecutionDepth--;
        }
    }

    private void ThrowIfHandlerWriteRejected()
    {
        if (_handlerWriteRejected != 0)
        {
            throw CreateHandlerWriteException();
        }
    }

    private static InvalidOperationException CreateHandlerWriteException() => new(
        "Journaled state cannot be committed or deleted from inside a durable inbox handler. "
        + "Handler effects, outgoing messages, and inbox completion are captured by the admitted journal operation.");

    public int Count => _inboxDict.Count;
    public int Capacity => _maxCapacity;

    public void RegisterHandler(string routeKey, IInboxHandler handler)
    {
        _durableInbox.RegisterHandler(routeKey, handler);
        LogHandlerRegistered(_logger, routeKey, _grainContext.GrainId);
    }

    public bool HasHandler(string routeKey) => _durableInbox.HasHandler(routeKey);
    public bool TryGetHandler(string routeKey, [MaybeNullWhen(false)] out IInboxHandler handler) =>
        _durableInbox.TryGetHandler(routeKey, out handler);

    public async ValueTask<DeliveryResult> DeliverAsync(DurableEnvelope envelope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReady();
        if (envelope.ReceiverId != _grainContext.GrainId)
        {
            throw new ArgumentException($"The envelope receiver '{envelope.ReceiverId}' does not match this grain '{_grainContext.GrainId}'.", nameof(envelope));
        }

        EnsureMetricsActive();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            ValidateReady();
            var key = (envelope.SenderId, envelope.MessageId);
            if (_processed.TryGetValue(key, out var processedAt)
                && !DurableMessagingTime.IsExpired(_timeProvider.GetUtcNow(), processedAt, _deduplicationWindow))
            {
                _instruments.OnInboxMessageReceived(_grainContext.GrainId.Type.ToString(), envelope.RouteKey, "duplicate");
                return DeliveryResult.Duplicate();
            }

            if (_inboxDict.ContainsKey(key))
            {
                await EnsureJobScheduledUnderGateAsync(CancellationToken.None).ConfigureAwait(true);
                ScheduleLocalDrain();
                return DeliveryResult.Duplicate();
            }

            if (_inboxDict.Count >= _maxCapacity)
            {
                _instruments.OnInboxMessageReceived(_grainContext.GrainId.Type.ToString(), envelope.RouteKey, "backpressured");
                return DeliveryResult.Backpressured();
            }

            if (!TryFindHandlerWithinMutationBoundary(new InboxHandlerSelectionContext(envelope, _grainContext.GrainId), out _))
            {
                _instruments.OnInboxMessageReceived(_grainContext.GrainId.Type.ToString(), envelope.RouteKey, "route_not_found");
                return DeliveryResult.RouteNotFound(envelope.RouteKey);
            }

            var generation = _stateGeneration;
            OwnershipProposal? proposal = null;
            try
            {
                if (GetDurableInboxCount() == 0 || !HasCommittedOwnership())
                {
                    proposal = await PrepareOwnershipAsync(CancellationToken.None).ConfigureAwait(true);
                }

                ValidateReady();
                var operation = new AcceptanceWrite(generation, envelope, proposal);
                await SubmitAsync(operation).ConfigureAwait(true);
                ValidateReady();
                ScheduleLocalDrain();
                _instruments.OnInboxMessageReceived(_grainContext.GrainId.Type.ToString(), envelope.RouteKey, "accepted");
                LogMessageAccepted(_logger, envelope.MessageId, envelope.SenderId, envelope.ReceiverId, envelope.RouteKey, envelope.CorrelationKey?.ToString());
                return DeliveryResult.Accepted();
            }
            finally
            {
                if (proposal is not null)
                {
                    _pendingOwnershipIds.Remove(proposal.Id);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<OwnershipProposal> PrepareOwnershipAsync(CancellationToken cancellationToken)
    {
        ValidateReady();
        var generation = _stateGeneration;
        var previousId = _jobId.Value;
        var previousJob = _job.Value;
        var sequence = checked(++_reservedSequence);
        var id = DurableMessagingJobOwnership.CreateId(_ownershipEpoch, sequence);
        _pendingOwnershipIds.Add(id);
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
            var job = await _jobManager.ScheduleJobAsync(new ScheduleJobRequest
            {
                Target = _grainContext.GrainId,
                JobName = JobName,
                DueTime = _jobTimeProvider.GetUtcNow(),
                Metadata = DurableMessagingJobOwnership.CreateMetadata(id)
            }, cancellation.Token).ConfigureAwait(true);
            ValidateReady();
            return new(id, sequence, job, generation, previousId, previousJob);
        }
        catch
        {
            _pendingOwnershipIds.Remove(id);
            _failure?.Throw();
            throw;
        }
    }

    private async ValueTask EnsureJobScheduledUnderGateAsync(CancellationToken cancellationToken)
    {
        ValidateReady();
        if (GetDurableInboxCount() == 0 || HasCommittedOwnership())
        {
            return;
        }

        var proposal = await PrepareOwnershipAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            await SubmitAsync(new OwnershipWrite(_stateGeneration, proposal)).ConfigureAwait(true);
        }
        finally
        {
            _pendingOwnershipIds.Remove(proposal.Id);
        }
    }

    private async ValueTask SubmitAsync(InboxWrite operation)
    {
        ValidateReady();
        _pendingWrites.Add(operation);
        try
        {
            // Admission and the descriptor publication share this grain turn. The hook never writes recursively.
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

    private void ValidateReady()
    {
        _failure?.Throw();
        ThrowIfOwnershipStateInvalid();
        _shutdownCts.Token.ThrowIfCancellationRequested();
        if (!_recoveryCompleted)
        {
            throw new InvalidOperationException("Durable inbox initialization has not completed.");
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

    private void ValidateGeneration(long generation)
    {
        ValidateReady();
        if (generation != _stateGeneration)
        {
            throw new InvalidOperationException("The admitted inbox operation belongs to an obsolete activation or deletion generation.");
        }
    }

    private bool HasCommittedOwnership() =>
        DurableMessagingJobOwnership.HasOwner(_jobId.Value, _job.Value)
        && string.Equals(_durableOwnershipId, _jobId.Value, StringComparison.Ordinal)
        && DurableMessagingJobOwnership.IsSamePhysicalJob(_durableJob, _job.Value);

    private bool IsCurrentOwner(PumpOwner owner) => owner.Generation == _stateGeneration
        && string.Equals(owner.Id, _jobId.Value, StringComparison.Ordinal)
        && DurableMessagingJobOwnership.IsSamePhysicalJob(owner.Job, _job.Value)
        && HasCommittedOwnership();

    private void ValidateOwner(PumpOwner owner)
    {
        ValidateGeneration(owner.Generation);
        if (!IsCurrentOwner(owner))
        {
            throw new InvalidOperationException("The admitted inbox operation no longer owns the acknowledged physical job.");
        }
    }

    private int GetDurableInboxCount() => _inboxDict.Keys.Count(key => !_provisionalAcceptances.Contains(key));

    public void OnWriteRequested()
    {
        _failure?.Throw();
        if (_handlerExecutionDepth != 0)
        {
            _handlerWriteRejected = 1;
            throw CreateHandlerWriteException();
        }
    }

    public void OnDeleteRequested()
    {
        OnWriteRequested();
        if (_pendingOwnershipIds.Count != 0 || _pendingWrites.Count != 0 || _admittedWrites.Length != 0)
        {
            throw new InvalidOperationException("Durable inbox operations must be quiescent before deleting journaled state.");
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
        _failure?.Throw();
        _admittedWrites = _pendingWrites.ToArray();
        _pendingWrites.Clear();
        foreach (var operation in _admittedWrites)
        {
            ValidateGeneration(operation.Generation);
            if (operation is HandlerWrite handler)
            {
                await PrepareHandlerAsync(handler, cancellationToken).ConfigureAwait(true);
            }
        }
    }

    private async ValueTask PrepareHandlerAsync(HandlerWrite operation, CancellationToken cancellationToken)
    {
        ValidateOwner(operation.Owner);
        if (!_inboxDict.ContainsKey(operation.Key))
        {
            throw new InvalidOperationException("The admitted inbox message is no longer pending.");
        }

        if (operation.Cancellation.IsCancellationRequested)
        {
            operation.Skipped = true;
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, operation.Cancellation, _shutdownCts.Token);
        try
        {
            if (!TryFindHandlerWithinMutationBoundary(new InboxHandlerSelectionContext(operation.Envelope, _grainContext.GrainId), out var handler))
            {
                operation.Error = new InvalidOperationException("No compatible handler is registered.");
                operation.DeadLetter = true;
                return;
            }

            _handlerWriteRejected = 0;
            _handlerExecutionDepth++;
            operation.HandlerInvoked = true;
            try
            {
                await handler.HandleAsync(new InboxHandlerContext(operation.Envelope, _grainContext.GrainId, _outbox, _sessionPool), cancellation.Token).ConfigureAwait(true);
            }
            finally
            {
                _handlerExecutionDepth--;
            }

            ThrowIfHandlerWriteRejected();
        }
        catch (Exception exception) when (!cancellation.IsCancellationRequested && _handlerWriteRejected == 0 && _failure is null)
        {
            // Conforming handlers report business failures before staging application effects.
            operation.Error = exception;
            LogHandlerException(_logger, exception, operation.Envelope.MessageId, operation.Envelope.SenderId,
                operation.Envelope.RouteKey, operation.Envelope.CorrelationKey?.ToString());
        }

        ValidateOwner(operation.Owner);
        if (operation.Error is not null)
        {
            var attempts = _messageStates.TryGetValue(operation.Key, out var state) ? state.AttemptCount : 0;
            var count = checked(attempts + 1);
            operation.DeadLetter = count >= _maxProcessingAttempts;
            operation.Retry = new InboxMessageState
            {
                AttemptCount = count,
                LastError = operation.Error.ToString(),
                NextAttemptAt = DurableMessagingTime.AddClamped(_timeProvider.GetUtcNow(),
                    TimeSpan.FromTicks(_retryDelay.Ticks * (1L << Math.Min(count - 1, DurableInboxOptions.MaximumBackoffExponent))))
            };
        }
    }

    public void FinalizeWrite(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var operation in _admittedWrites)
        {
            ValidateGeneration(operation.Generation);
            switch (operation)
            {
                case AcceptanceWrite acceptance:
                    if (_inboxDict.ContainsKey(acceptance.Key) || _inboxDict.Count >= _maxCapacity)
                    {
                        throw new InvalidOperationException("The admitted inbox acceptance no longer matches available capacity or message identity.");
                    }

                    if (acceptance.Owner is { } acceptanceOwner)
                    {
                        ApplyOwnership(acceptanceOwner);
                    }
                    else if (!HasCommittedOwnership())
                    {
                        throw new InvalidOperationException("The admitted inbox acceptance requires acknowledged job ownership.");
                    }

                    _processed.Remove(acceptance.Key);
                    _inboxDict.Add(acceptance.Key, acceptance.Envelope);
                    _messageStates.Add(acceptance.Key, new InboxMessageState());
                    _provisionalAcceptances.Add(acceptance.Key);
                    UpdateInboxDepth(1);
                    break;
                case OwnershipWrite ownership:
                    if (GetDurableInboxCount() == 0)
                    {
                        throw new InvalidOperationException("The admitted ownership repair has no pending inbox work.");
                    }
                    ApplyOwnership(ownership.Owner);
                    break;
                case HandlerWrite handler:
                    ValidateOwner(handler.Owner);
                    if (handler.Skipped)
                    {
                        break;
                    }

                    if (!_inboxDict.ContainsKey(handler.Key))
                    {
                        throw new InvalidOperationException("The invoked inbox handler lost its pending message before capture.");
                    }

                    if (handler.Error is not null && !handler.DeadLetter)
                    {
                        _messageStates[handler.Key] = handler.Retry!;
                    }
                    else
                    {
                        var now = _timeProvider.GetUtcNow();
                        if (handler.DeadLetter)
                        {
                            DurableDeadLetterRetention.Compact(_deadLetters, now, _deadLetterRetentionPeriod,
                                _maxRetainedDeadLetters, static entry => entry.DeadLetteredAt,
                                reservedCapacity: _deadLetters.ContainsKey(handler.Key) ? 0 : 1);
                            _deadLetters[handler.Key] = new InboxDeadLetter
                            {
                                Envelope = handler.Envelope,
                                DeadLetteredAt = now,
                                Reason = handler.Error!.Message,
                                AttemptCount = handler.Retry?.AttemptCount ?? 0
                            };
                        }
                        RemoveMessage(handler.Key);
                        _messageStates.Remove(handler.Key);
                        _processed[handler.Key] = now;
                        TrackProcessedExpiry(now);
                    }
                    break;
                case ClearOwnerWrite clear:
                    ValidateOwner(clear.Owner);
                    if (_inboxDict.Count != 0)
                    {
                        throw new InvalidOperationException("The admitted inbox owner cannot be cleared while work is pending.");
                    }
                    _completedJobId.Value = clear.Owner.Id;
                    _jobId.Value = null;
                    _job.Value = null;
                    break;
                case CompactWrite:
                    DurableDeadLetterRetention.Compact(_deadLetters, _timeProvider.GetUtcNow(), _deadLetterRetentionPeriod,
                        _maxRetainedDeadLetters, static entry => entry.DeadLetteredAt);
                    break;
            }
        }

        var maintenanceTime = _timeProvider.GetUtcNow();
        if (IsProcessedMaintenanceDue(maintenanceTime))
        {
            CompactProcessedMessages(maintenanceTime);
        }
    }

    private void ApplyOwnership(OwnershipProposal proposal)
    {
        ValidateGeneration(proposal.Generation);
        if (!string.Equals(_jobId.Value, proposal.PreviousId, StringComparison.Ordinal)
            || !(proposal.PreviousJob is null && _job.Value is null
                || DurableMessagingJobOwnership.IsSamePhysicalJob(proposal.PreviousJob, _job.Value)))
        {
            throw new InvalidOperationException("The admitted inbox ownership proposal no longer matches the preceding owner.");
        }
        _jobId.Value = proposal.Id;
        _job.Value = proposal.Job;
        _jobSequence.Value = proposal.Sequence;
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
        _committingOwnershipId = null;
        _committingJob = null;
        _committingCompletedJobId = null;
        foreach (var operation in _admittedWrites)
        {
            if (operation is AcceptanceWrite acceptance)
            {
                _provisionalAcceptances.Remove(acceptance.Key);
            }
            operation.Completed.TrySetResult();
        }
        _admittedWrites = [];
    }

    public void OnFaulted(Exception exception)
    {
        _failure ??= ExceptionDispatchInfo.Capture(exception);
        _pumpCoordinator.Reset();
        try
        {
            _shutdownCts.Cancel();
        }
        finally
        {
            foreach (var operation in _admittedWrites.Concat(_pendingWrites))
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
        _lastProcessedCompaction = null;
        RebuildProcessedExpiry();
        _recoveryCompleted = true;
        ReconcileInboxDepth();
    }

    public void OnDeleteCompleted()
    {
        _pumpCoordinator.Reset();
        _stateGeneration++;
        _ownershipEpoch = Guid.NewGuid().ToString("N");
        _reservedSequence = _jobSequence.Value;
        _durableOwnershipId = null;
        _durableJob = null;
        _durableCompletedJobId = null;
        _ownershipStateError = null;
        _nextProcessedExpiry = null;
        _lastProcessedCompaction = null;
        ReconcileInboxDepth();
    }

    internal async Task ResumeProcessingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            ValidateReady();
            EnsureMetricsActive();
            await EnsureJobScheduledUnderGateAsync(cancellationToken).ConfigureAwait(true);
            ScheduleLocalDrain();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task OnStart(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DurableMessagingActivationValidator.Validate(_grainContext);
        await ResumeProcessingAsync(cancellationToken).ConfigureAwait(true);
        if (_deadLetters.Values.Any(entry => DurableMessagingTime.IsExpired(_timeProvider.GetUtcNow(), entry.DeadLetteredAt, _deadLetterRetentionPeriod))
            || _deadLetters.Count > _maxRetainedDeadLetters
            || HasExpiredProcessedMessages(_timeProvider.GetUtcNow()))
        {
            await SubmitAsync(new CompactWrite(_stateGeneration)).ConfigureAwait(true);
        }
    }

    public Task OnStop(CancellationToken cancellationToken)
    {
        StopProcessing();
        return Task.CompletedTask;
    }

    internal void StopProcessing()
    {
        _shutdownCts.Cancel();
        _pumpCoordinator.Reset();
        if (Interlocked.Exchange(ref _metricsActive, 0) != 0)
        {
            _instruments.OnInboxDepthChanged(-Interlocked.Exchange(ref _reportedDepth, 0));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            StopProcessing();
            _shutdownCts.Dispose();
        }
    }

    private readonly record struct PumpOwner(string Id, DurableJob Job, long Generation);
    private sealed record OwnershipProposal(string Id, long Sequence, DurableJob Job, long Generation, string? PreviousId, DurableJob? PreviousJob);

    private abstract class InboxWrite(long generation)
    {
        public long Generation { get; } = generation;
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class AcceptanceWrite(long generation, DurableEnvelope envelope, OwnershipProposal? owner) : InboxWrite(generation)
    {
        public DurableEnvelope Envelope { get; } = envelope;
        public (GrainId, Guid) Key => (Envelope.SenderId, Envelope.MessageId);
        public OwnershipProposal? Owner { get; } = owner;
    }

    private sealed class OwnershipWrite(long generation, OwnershipProposal owner) : InboxWrite(generation)
    {
        public OwnershipProposal Owner { get; } = owner;
    }

    private sealed class HandlerWrite(PumpOwner owner, DurableEnvelope envelope, CancellationToken cancellation) : InboxWrite(owner.Generation)
    {
        public PumpOwner Owner { get; } = owner;
        public DurableEnvelope Envelope { get; } = envelope;
        public (GrainId, Guid) Key => (Envelope.SenderId, Envelope.MessageId);
        public CancellationToken Cancellation { get; } = cancellation;
        public bool HandlerInvoked { get; set; }
        public bool Skipped { get; set; }
        public bool DeadLetter { get; set; }
        public Exception? Error { get; set; }
        public InboxMessageState? Retry { get; set; }
    }

    private sealed class ClearOwnerWrite(PumpOwner owner) : InboxWrite(owner.Generation)
    {
        public PumpOwner Owner { get; } = owner;
    }

    private sealed class CompactWrite(long generation) : InboxWrite(generation);

    public async ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        _failure?.Throw();
        if (!_recoveryCompleted)
        {
            return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
        }
        ThrowIfOwnershipStateInvalid();
        if (!DurableMessagingJobOwnership.TryGetOwnershipId(context.Job, out var ownershipId))
        {
            return DurableJobRunResult.Completed;
        }

        if (_pendingOwnershipIds.Count != 0 || _admittedWrites.Any(static operation => operation is ClearOwnerWrite))
        {
            return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
        }

        if (!string.Equals(_jobId.Value, ownershipId, StringComparison.Ordinal))
        {
            var disposition = DurableMessagingJobOwnership.ResolveMismatch(
                _recoveryCompleted,
                HasCommittedOwnership(),
                DurableMessagingJobOwnership.IsCompleted(_durableCompletedJobId, ownershipId),
                _inboxDict.Count > 0);
            if (disposition == OwnershipMismatchDisposition.ReclaimOrphan)
            {
                LogOrphanedJobReclaimed(_logger, ownershipId, _grainContext.GrainId);
                _instruments.OnOrphanedJobReclaimed(_grainContext.GrainId.Type.ToString(), JobName);
                return DurableJobRunResult.Completed;
            }

            if (disposition == OwnershipMismatchDisposition.CompleteStale)
            {
                return DurableJobRunResult.Completed;
            }

            return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
        }

        if (!HasCommittedOwnership())
        {
            return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
        }

        if (!DurableMessagingJobOwnership.IsSamePhysicalJob(_job.Value, context.Job))
        {
            return DurableJobRunResult.Completed;
        }

        var key = new DurableMessagingPumpExecutionKey(
            JobName,
            context.Job.Id,
            context.RunId,
            Volatile.Read(ref _stateGeneration));
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

        var state = new PumpTimerState(
            this,
            execution,
            lease,
            new PumpOwner(ownershipId, context.Job, _stateGeneration),
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
            _pumpCoordinator.Release(lease);
            _pumpResults.Fail(execution, registrationException);
            throw;
        }

        return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
    }

    private async Task RunPumpTimerAsync(
        DurableMessagingPumpExecution execution,
        DurableMessagingPumpLease lease,
        PumpOwner pumpOwner,
        CancellationToken jobCancellation,
        CancellationToken timerCancellation)
    {
        if (!_pumpCoordinator.IsCurrent(lease) || !_pumpResults.TryBegin(execution))
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
                _shutdownCts.Token);
            result = await ExecuteJobCoreAsync(
                pumpOwner,
                clearOwnershipWhenEmpty: true,
                linkedCancellation.Token);
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

    private async ValueTask<DurableJobRunResult> ExecuteJobCoreAsync(PumpOwner owner, bool clearOwnershipWhenEmpty, CancellationToken cancellationToken)
    {
        ValidateReady();
        if (!IsCurrentOwner(owner))
        {
            return DurableJobRunResult.Completed;
        }

        var now = _timeProvider.GetUtcNow();
        var pending = _inboxDict.Where(pair => !_provisionalAcceptances.Contains(pair.Key)
                && (!_messageStates.TryGetValue(pair.Key, out var state) || state.NextAttemptAt is null || state.NextAttemptAt <= now))
            .Take(_batchSize).Select(static pair => pair.Value).ToList();
        foreach (var envelope in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateOwner(owner);
            var stopwatch = Stopwatch.StartNew();
            var operation = new HandlerWrite(owner, envelope, cancellationToken);
            await SubmitAsync(operation).ConfigureAwait(true);
            if (operation.Skipped)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var status = operation.Error is null ? "success" : operation.DeadLetter ? "dead_lettered" : "retry";
            _instruments.OnInboxMessageProcessed(_grainContext.GrainId.Type.ToString(), envelope.RouteKey, status);
            _instruments.OnInboxProcessingDuration(stopwatch.Elapsed, _grainContext.GrainId.Type.ToString(), envelope.RouteKey);
        }

        ValidateOwner(owner);
        if (_inboxDict.Count == 0)
        {
            if (clearOwnershipWhenEmpty)
            {
                await SubmitAsync(new ClearOwnerWrite(owner)).ConfigureAwait(true);
            }
            return DurableJobRunResult.Completed;
        }

        if (HasExpiredProcessedMessages(_timeProvider.GetUtcNow()))
        {
            await SubmitAsync(new CompactWrite(_stateGeneration)).ConfigureAwait(true);
        }

        var nextAttempt = GetNextAttemptAt();
        if (GetNextProcessedMaintenance() is { } maintenance && maintenance < nextAttempt)
        {
            nextAttempt = maintenance;
        }
        var delay = nextAttempt - _timeProvider.GetUtcNow();
        return DurableJobRunResult.RescheduleAt(DurableMessagingTime.AddClamped(
            _jobTimeProvider.GetUtcNow(), delay > TimeSpan.Zero ? delay : TimeSpan.Zero));
    }

    private DateTimeOffset GetNextAttemptAt()
    {
        var now = _timeProvider.GetUtcNow();
        var attempts = _inboxDict.Keys.Select(key => _messageStates.TryGetValue(key, out var state) ? state.NextAttemptAt : null).ToList();
        return attempts.Any(value => value is null || value <= now) ? now : attempts.Min()!.Value;
    }

    private DateTimeOffset? GetNextProcessedMaintenance()
    {
        if (_nextProcessedExpiry is not { } next)
        {
            return null;
        }
        if (_lastProcessedCompaction is { } last)
        {
            if (_processedCompactionInterval.Ticks > DateTimeOffset.MaxValue.UtcTicks - last.UtcTicks)
            {
                return null;
            }
            var earliestScan = DurableMessagingTime.AddClamped(last, _processedCompactionInterval);
            if (earliestScan > next)
            {
                next = earliestScan;
            }
        }
        return next;
    }

    private bool IsProcessedMaintenanceDue(DateTimeOffset now) =>
        GetNextProcessedMaintenance() is { } next && now >= next;

    private bool HasExpiredProcessedMessages(DateTimeOffset now)
    {
        if (!IsProcessedMaintenanceDue(now))
        {
            return false;
        }
        if (_processed.Any(pair => DurableMessagingTime.IsExpired(now, pair.Value, _deduplicationWindow)))
        {
            return true;
        }

        _lastProcessedCompaction = now;
        RebuildProcessedExpiry();
        return false;
    }

    private void TrackProcessedExpiry(DateTimeOffset processedAt)
    {
        if (_deduplicationWindow.Ticks > DateTimeOffset.MaxValue.UtcTicks - processedAt.UtcTicks)
        {
            return;
        }
        var expiry = DurableMessagingTime.AddClamped(processedAt, _deduplicationWindow);
        if (_nextProcessedExpiry is null || expiry < _nextProcessedExpiry)
        {
            _nextProcessedExpiry = expiry;
        }
    }

    private void RebuildProcessedExpiry()
    {
        _nextProcessedExpiry = null;
        foreach (var entry in _processed)
        {
            TrackProcessedExpiry(entry.Value);
        }
    }

    private void CompactProcessedMessages(DateTimeOffset now)
    {
        // Amortize dictionary scans over retention time; ordinary completion and owner-clear writes only check the deadline.
        List<(GrainId, Guid)>? expired = null;
        _nextProcessedExpiry = null;
        foreach (var entry in _processed)
        {
            if (DurableMessagingTime.IsExpired(now, entry.Value, _deduplicationWindow))
            {
                (expired ??= []).Add(entry.Key);
            }
            else
            {
                TrackProcessedExpiry(entry.Value);
            }
        }
        if (expired is not null)
        {
            foreach (var key in expired)
            {
                _processed.Remove(key);
            }
        }
        _lastProcessedCompaction = now;
    }

    private void EnsureMetricsActive()
    {
        if (Interlocked.Exchange(ref _metricsActive, 1) == 0)
        {
            Volatile.Write(ref _reportedDepth, _inboxDict.Count);
            _instruments.OnInboxDepthChanged(_inboxDict.Count);
        }
    }

    private void UpdateInboxDepth(int delta)
    {
        if (Volatile.Read(ref _metricsActive) != 0)
        {
            Interlocked.Add(ref _reportedDepth, delta);
            _instruments.OnInboxDepthChanged(delta);
        }
    }

    private void ReconcileInboxDepth()
    {
        if (Volatile.Read(ref _metricsActive) == 0)
        {
            return;
        }

        var count = _inboxDict.Count;
        var delta = count - Interlocked.Exchange(ref _reportedDepth, count);
        if (delta != 0)
        {
            _instruments.OnInboxDepthChanged(delta);
        }
    }

    private bool RemoveMessage((GrainId SenderId, Guid MessageId) key)
    {
        if (!_inboxDict.Remove(key))
        {
            return false;
        }

        UpdateInboxDepth(-1);

        return true;
    }

    // Structured logging using LoggerMessage source generator

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Registered handler for route '{RouteKey}' on grain {GrainId}")]
    private static partial void LogHandlerRegistered(ILogger logger, string routeKey, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Duplicate message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogDuplicateMessageDetected(ILogger logger, Guid messageId, GrainId senderId, GrainId receiverId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Duplicate message {MessageId} from {SenderId} already in inbox for {ReceiverId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogDuplicateMessageInInbox(ILogger logger, Guid messageId, GrainId senderId, GrainId receiverId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Inbox at capacity ({Count}/{Capacity}) for grain {GrainId}, rejecting message {MessageId} from {SenderId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogBackpressureRejection(ILogger logger, int count, int capacity, GrainId grainId, Guid messageId, GrainId senderId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No handler registered for route '{RouteKey}' on grain {GrainId}, rejecting message {MessageId} from {SenderId} (CorrelationKey: {CorrelationKey})")]
    private static partial void LogRouteNotFound(ILogger logger, string routeKey, GrainId grainId, Guid messageId, GrainId senderId, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Accepted message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogMessageAccepted(ILogger logger, Guid messageId, GrainId senderId, GrainId receiverId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error processing message {MessageId} from {SenderId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogProcessingError(ILogger logger, Exception exception, Guid messageId, GrainId senderId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Handler for route '{RouteKey}' not found during processing of message {MessageId} (CorrelationKey: {CorrelationKey})")]
    private static partial void LogHandlerNotFoundDuringProcessing(ILogger logger, string routeKey, Guid messageId, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Processed message {MessageId} from {SenderId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogMessageProcessed(ILogger logger, Guid messageId, GrainId senderId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Handler threw exception for message {MessageId} from {SenderId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogHandlerException(ILogger logger, Exception exception, Guid messageId, GrainId senderId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Reclaimed orphaned inbox job ownership {OwnershipId} for grain {GrainId}")]
    private static partial void LogOrphanedJobReclaimed(ILogger logger, string ownershipId, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error scheduling durable inbox recovery for grain {GrainId}")]
    private static partial void LogRecoverySchedulingError(ILogger logger, Exception exception, GrainId grainId);

    private sealed class PumpTimerState(
        DurableInboxExtension owner,
        DurableMessagingPumpExecution execution,
        DurableMessagingPumpLease lease,
        PumpOwner pumpOwner,
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
                    pumpOwner,
                    jobCancellation,
                    timerCancellation);
            }
            finally
            {
                owner._pumpCoordinator.Release(lease);
                Handle.Complete();
            }
        }
    }

    private void ScheduleLocalDrain()
    {
        if (_jobId.Value is not { Length: > 0 } jobId
            || GetDurableInboxCount() == 0
            || !HasCommittedOwnership())
        {
            return;
        }

        if (!_pumpCoordinator.TryAcquire(jobId, _shutdownCts.Token, out var lease))
        {
            return;
        }

        var state = new LocalDrainTimerState(this, lease, new PumpOwner(jobId, _job.Value!, _stateGeneration));
        try
        {
            state.Handle.Attach(_timerRegistry.RegisterGrainTimer(
                _grainContext,
                static (state, cancellationToken) => state.RunAsync(cancellationToken),
                state,
                new GrainTimerCreationOptions(TimeSpan.Zero, Timeout.InfiniteTimeSpan)
                {
                    Interleave = false,
                    KeepAlive = true
                }));
        }
        catch
        {
            _pumpCoordinator.Release(lease);
            throw;
        }
    }

    private sealed class LocalDrainTimerState(DurableInboxExtension owner, DurableMessagingPumpLease lease, PumpOwner pumpOwner)
    {
        public OneShotTimerHandle Handle { get; } = new();

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (owner._pumpCoordinator.IsCurrent(lease))
                {
                    _ = await owner.ExecuteJobCoreAsync(
                        pumpOwner,
                        clearOwnershipWhenEmpty: false,
                        cancellationToken);
                }
            }
            finally
            {
                owner._pumpCoordinator.Release(lease);
                Handle.Complete();
            }
        }
    }

}

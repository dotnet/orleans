using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Durable.Journaling;
using Orleans.Durable.Messaging.Configuration;
using Orleans.Durable.Messaging.Diagnostics;
using Orleans.Runtime;

namespace Orleans.Durable.Messaging;

/// <summary>
/// Accepts, deduplicates, and processes durable incoming messages for one grain.
/// </summary>
/// <remarks>
/// All state transitions run on the activation scheduler, so each check-then-emit sequence is atomic.
/// Accepted messages are processed only after their acceptance is durable and their <see cref="Envelope.DeliverAt"/>,
/// if any, has passed.
/// </remarks>
internal sealed partial class Inbox : IInbox, IMessagingExtension, IStateMachine, IReportsStagedChanges, IDurableValueCommandHandler<InboxCommand>
{
    private readonly MessagingPump _pump;
    private readonly InboxDispatcher _handlers = new([]);
    private readonly List<IInboxMessageSink> _sinks = [];
    private readonly List<Func<TimeSpan?>> _wakeupSources = [];

    public Inbox(MessagingPump pump)
    {
        _pump = pump;
        pump.StateManager.RegisterStateMachine(MessagingStateNames.Inbox, this);
        pump.Register(this);

        // Deliveries reach only an inbox which recovers with the journal, so an activation which never resolves its
        // inbox has no messaging extension.
        pump.GrainContext.SetComponent<IMessagingExtension>(this);
    }

    private MessagingShared Shared => _pump.Shared;
    private DurableMessagingOptions Options => _pump.Shared.Options;
    private ILogger Logger => _pump.Shared.InboxLogger;
    private TimeProvider TimeProvider => _pump.Shared.TimeProvider;
    private IGrainContext GrainContext => _pump.GrainContext;

    internal int Count => _messages.Count;

    internal IReadOnlyDictionary<MessageId, InboxEntry> MessageState => _messages;

    internal IReadOnlyDictionary<MessageId, InboxDeadLetterRecord> DeadLetterState => _deadLetters;

    internal InboxDispatcher Handlers => _handlers;

    public IInbox Register(IInboxHandler handler)
    {
        _handlers.Add(handler);

        // Pending messages wait for a handler without holding a wake-up, so a late registration resumes them.
        if (_messages.Count > 0) _pump.RequestWakeup();
        return this;
    }

    public IInbox Register(IInboxMessageSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sinks.Add(sink);
        return this;
    }

    public IInbox RegisterWakeup(Func<TimeSpan?> getWakeupDelay)
    {
        ArgumentNullException.ThrowIfNull(getWakeupDelay);
        _wakeupSources.Add(getWakeupDelay);
        return this;
    }

    public async ValueTask<DeliveryResult> DeliverAsync(InboxMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _pump.ShutdownToken.ThrowIfCancellationRequested();
        EnvelopeValidation.Validate(message);

        var key = message.Envelope.MessageId;
        var receiverTime = Shared.JobTimeProvider.GetUtcNow();
        // Recorded outcomes and expiration decisions survive clock rollback, so only a time the inbox would admit is bounded.
        if (message.SentAt > MessagingTime.AddClamped(receiverTime, Options.DeduplicationWindow)
            && !_processed.ContainsKey(key)
            && !_messages.ContainsKey(key)
            && message.SentAt > GetAdmissionFloor(receiverTime))
        {
            LogSentAtTooFarAhead(Logger, key, GrainContext.GrainId, message.SentAt, receiverTime);
            throw new ArgumentOutOfRangeException(nameof(message), message.SentAt,
                "SentAt must not be later than the receiver's clock plus DeduplicationWindow.");
        }

        // Delivery is a grain call, so the activation is servicing requests.
        _pump.EnsureStarted();
        if (IsDuplicate(message, out var pending))
        {
            var rejection = pending is null ? _processed[key].Rejection : null;
            // The sender may retire its copy once this returns, so the acceptance or the processed record must be
            // durable. A completion staged by a handler or a sink can still be in flight.
            if (pending is null ? !AreCompletionsDurable : !IsDurable(pending)) await PersistForCallerAsync(cancellationToken).ConfigureAwait(true);
            if (pending is not null) _pump.Signal();

            Shared.Instruments.OnInboxMessageReceived(_pump.GrainTypeTag, "duplicate");

            // A sender whose answer was lost delivers again, so it receives the rejection which a sink first answered.
            return rejection is not null
                ? new DeliveryResult.DeadLettered(rejection.Reason, rejection.Code)
                : DeliveryResult.Duplicate.Instance;
        }

        // Retained identities precede expiration: a lost acknowledgement may belong to pending or completed work.
        // Only a durable, non-decreasing floor permits forgetting that evidence, regardless of later clock motion.
        var floor = GetAdmissionFloor(receiverTime);
        if (message.SentAt <= floor)
        {
            if (message.SentAt > _admissionFloor) Emit(new InboxCommand.AdvanceAdmissionFloor(floor));
            if (_admissionFloor > _durableAdmissionFloor) await PersistForCallerAsync(cancellationToken).ConfigureAwait(true);
            Shared.Instruments.OnInboxMessageReceived(_pump.GrainTypeTag, "expired");
            LogMessageExpired(Logger, key, GrainContext.GrainId, message.SentAt, _admissionFloor);
            return DeliveryResult.Expired.Instance;
        }

        MessagingEvents.EmitInboxMessageReceiving(GrainContext, message);

        // Sink-owned messages never occupy inbox capacity and need no handler. A sink's duplicate can still be staged,
        // so every outcome except backpressure is reported once the write is durable.
        if (StageSinkAcceptance(message) is { } sinkResult)
        {
            if (sinkResult is not DeliveryResult.Backpressured) await PersistForCallerAsync(cancellationToken).ConfigureAwait(true);
            return sinkResult;
        }

        // Every pending message, scheduled or not, holds capacity until it completes.
        if (_messages.Count >= Options.MaxCapacity)
        {
            Shared.Instruments.OnInboxMessageReceived(_pump.GrainTypeTag, "backpressured");
            return DeliveryResult.Backpressured.Instance;
        }

        if (_handlers.IsEmpty)
        {
            Shared.Instruments.OnInboxMessageReceived(_pump.GrainTypeTag, "handler_not_found");
            LogHandlerNotFound(Logger, key, GrainContext.GrainId);
            return DeliveryResult.HandlerNotFound.Instance;
        }

        Emit(new InboxCommand.Accept(message));
        await PersistForCallerAsync(cancellationToken).ConfigureAwait(true);
        Shared.Instruments.OnInboxMessageReceived(_pump.GrainTypeTag, "accepted");
        LogMessageAccepted(Logger, key, GrainContext.GrainId);
        return DeliveryResult.Accepted.Instance;
    }

    /// <summary>
    /// Runs the handlers in a flow whose staged changes the attempt owns. Requests and other turns which interleave
    /// while a handler awaits run in their own flows, so the attempt never treats their changes as its own.
    /// </summary>
    private async ValueTask HandleAsync(InboxEntry entry, HandlerExecution execution, CancellationToken cancellationToken)
    {
        execution.StagedChanges = _pump.BeginStagedChangeScope();
        await _handlers.HandleAsync(new InboxHandlerContext(entry.Message, execution), cancellationToken).ConfigureAwait(true);
    }

    private static bool StagedDuring(HandlerExecution execution) => execution.StagedChanges!.HasStagedChanges;

    /// <summary>
    /// Stages acceptance of a message which this grain sends to itself, for the caller's next journal write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applies the identity rules of <see cref="DeliverAsync"/> synchronously and without writing: a completed or
    /// equivalent pending message is a duplicate, and a different pending command with the same ID conflicts.
    /// The acceptance becomes durable atomically with the caller's other staged state, after which the inbox processes
    /// the message as usual. When the pump is not running yet, the durable wake-up which the write ensures for new
    /// acceptances starts it once the activation services calls, so handlers never run during activation.
    /// </para>
    /// <para>
    /// A synchronous send cannot report backpressure or a missing handler, so the message is admitted regardless of
    /// <see cref="DurableMessagingOptions.MaxCapacity"/> and of handler registration, and a sink which backpressures
    /// it fails the send. The message still occupies capacity for deliveries from other grains until it completes, and
    /// processing waits for a handler to be registered.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The inbox holds a different pending command with the same ID, or an inbox sink backpressured the message or
    /// reported an unsupported result.
    /// </exception>
    internal void StageLocal(InboxMessage message)
    {
        // Reuse the outbox's clock observation: a fresh read could observe another rollback.
        var now = message.SentAt;
        // A local send commits with the inbox, so it can use the first admitted time after a clock rollback.
        if (message.SentAt <= _admissionFloor)
        {
            if (_admissionFloor == DateTimeOffset.MaxValue) throw new InvalidOperationException("The inbox admission floor leaves no representable send time for a new local message.");
            message = message with { SentAt = _admissionFloor.AddTicks(1) };
        }
        Debug.Assert(message.SentAt <= MessagingTime.AddClamped(now, Options.DeduplicationWindow)
            || message.SentAt <= _admissionFloor.AddTicks(1));
        if (IsDuplicate(message, out _))
        {
            Shared.Instruments.OnInboxMessageReceived(_pump.GrainTypeTag, "duplicate");
            return;
        }

        if (StageSinkAcceptance(message) is { } sinkResult)
        {
            if (sinkResult is DeliveryResult.Backpressured)
            {
                throw new InvalidOperationException(
                    $"An inbox message sink backpressured message '{message.Envelope.MessageId}', which the grain sent to itself.");
            }

            return;
        }

        try
        {
            Emit(new InboxCommand.Accept(message));
        }
        catch (Exception exception)
        {
            LogStagingFailed(Logger, exception);
            _pump.Fail(exception, "Inbox staging failed.");
            throw;
        }

        Shared.Instruments.OnInboxMessageReceived(_pump.GrainTypeTag, "accepted");
        LogMessageAccepted(Logger, message.Envelope.MessageId, GrainContext.GrainId);
    }

    /// <summary>
    /// Offers a message to the registered sinks and stages the outcome which the first sink to take the message reports.
    /// </summary>
    /// <returns>The delivery result, or <see langword="null"/> when every sink declines the message.</returns>
    /// <remarks>
    /// An accepted message journals only its processed record, in the write which commits the sink's staged changes.
    /// A duplicate or backpressured message stages nothing. A sink which rejects the message stages nothing itself, so
    /// the inbox records the rejection as the message's processed outcome. A sink exception, or an unsupported
    /// result, leaves the outcome unknown: it propagates to the caller with nothing staged, so the sender retries.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A sink reported an unsupported delivery result.</exception>
    private DeliveryResult? StageSinkAcceptance(InboxMessage message)
    {
        var key = message.Envelope.MessageId;
        foreach (var sink in _sinks)
        {
            if (!sink.TryAccept(message, out var result)) continue;
            DeliveryResult.DeadLettered? rejection = null;
            switch (result)
            {
                case DeliveryResult.Duplicate or DeliveryResult.Backpressured:
                    Shared.Instruments.OnInboxMessageReceived(_pump.GrainTypeTag, result is DeliveryResult.Duplicate ? "duplicate" : "backpressured");
                    return result;
                case DeliveryResult.DeadLettered deadLettered:
                    rejection = deadLettered;
                    break;
                case DeliveryResult.Accepted:
                    break;
                default:
                    throw new InvalidOperationException(
                        $"The inbox message sink reported the unsupported delivery result '{result.Value?.GetType().Name ?? "none"}'.");
            }

            var accepted = rejection is null;
            try
            {
                if (rejection is null)
                {
                    StageCompletion(message, deadLetter: null);
                }
                else
                {
                    LogSinkRejected(Logger, key, GrainContext.GrainId, rejection.Reason, rejection.Code);
                    StageCompletion(message, deadLetter: null, new() { Reason = rejection.Reason, Code = rejection.Code });
                }
            }
            catch (Exception exception)
            {
                // The sink's staged changes cannot commit without the processed record.
                LogStagingFailed(Logger, exception);
                _pump.Fail(exception, "Inbox staging failed.");
                throw;
            }

            Shared.Instruments.OnInboxMessageReceived(_pump.GrainTypeTag, accepted ? "accepted" : "dead_lettered");
            Shared.Instruments.OnInboxMessageProcessed(_pump.GrainTypeTag, accepted ? "success" : "dead_lettered");
            if (rejection is not null) return rejection;
            LogMessageAccepted(Logger, key, GrainContext.GrainId);
            return DeliveryResult.Accepted.Instance;
        }

        return null;
    }

    /// <summary>
    /// Recognizes a retained or pending identity, which keeps its recorded send time whatever the delivery's send time.
    /// </summary>
    /// <param name="message">The received message.</param>
    /// <param name="pending">The pending entry, or <see langword="null"/> when the message was completed or is new.</param>
    private bool IsDuplicate(InboxMessage message, out InboxEntry? pending)
    {
        var key = message.Envelope.MessageId;
        if (_processed.ContainsKey(key))
        {
            pending = null;
            return true;
        }

        if (_messages.TryGetValue(key, out pending))
        {
            if (!EnvelopeEquivalence.AreSameCommand(pending.Message, message))
            {
                throw new InvalidOperationException(
                    $"The inbox already contains a different command with message ID '{key}'.");
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Processes one batch of durable, due messages and returns whether any message was processed.
    /// </summary>
    internal async ValueTask<bool> ProcessReadyAsync(CancellationToken cancellationToken)
    {
        if (_handlers.IsEmpty || _messages.Count == 0)
        {
            return false;
        }

        var jobNow = Shared.JobTimeProvider.GetUtcNow();
        var batch = new List<InboxEntry>(Math.Min(Options.InboxBatchSize, _messages.Count));
        foreach (var entry in _messages.Values)
        {
            if (IsDurable(entry) && GetDelay(entry, jobNow) == TimeSpan.Zero)
            {
                batch.Add(entry);
                if (batch.Count == Options.InboxBatchSize) break;
            }
        }

        if (batch.Count == 0)
        {
            return false;
        }

        foreach (var entry in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessAsync(entry, cancellationToken).ConfigureAwait(true);
        }

        await PersistAsync().ConfigureAwait(true);
        return true;
    }

    private async ValueTask ProcessAsync(InboxEntry entry, CancellationToken cancellationToken)
    {
        if (!IsCurrent(entry))
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var execution = new HandlerExecution(this, entry) { Active = true };
        Exception? error = null;
        try
        {
            await HandleAsync(entry, execution, cancellationToken).ConfigureAwait(true);
            execution.ThrowIfRejected();
            if (!execution.Completed)
            {
                throw new InvalidOperationException("Inbox handlers must call Complete or Fail before returning successfully.");
            }
        }
        catch (Exception) when (execution.Rejection is { } rejection)
        {
            _pump.Fail(rejection.SourceException, "Inbox operation failed.");
            rejection.Throw();
        }
        catch (Exception exception) when (execution.Completed)
        {
            LogHandlerException(Logger, exception, entry.Message.Envelope.MessageId, GrainContext.GrainId);
        }
        catch (Exception exception) when (StagedDuring(execution))
        {
            // The attempt ended without an outcome after it staged changes. Pending changes share one journal buffer, so a
            // retry record would commit them; failing the activation discards them, and the message runs again after
            // recovery from the journal.
            LogHandlerException(Logger, exception, entry.Message.Envelope.MessageId, GrainContext.GrainId);
            var failure = new InvalidOperationException(
                $"The inbox handler for message '{entry.Message.Envelope.MessageId}' failed after staging changes, so the activation discards them.",
                exception);
            _pump.Fail(failure, "An inbox handler failed after staging changes.");
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            throw failure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            error = exception;
            LogHandlerException(Logger, exception, entry.Message.Envelope.MessageId, GrainContext.GrainId);
        }
        finally
        {
            execution.Active = false;
        }

        string status;
        if (error is not null)
        {
            if (!IsCurrent(entry))
            {
                return;
            }

            status = StageFailedAttempt(entry, error.Message, Shared.JobTimeProvider.GetUtcNow());
        }
        else
        {
            status = execution.DeadLetterReason is null ? "success" : "dead_lettered";
        }

        Shared.Instruments.OnInboxMessageProcessed(_pump.GrainTypeTag, status);
        Shared.Instruments.OnInboxProcessingDuration(Stopwatch.GetElapsedTime(start), _pump.GrainTypeTag);
    }

    /// <summary>
    /// Stages a failed attempt's outcome: a retry with backoff, or a dead letter once <c>InboxRetryTimeout</c> has elapsed
    /// since the message's first failure. Returns the processing status for metrics.
    /// </summary>
    /// <remarks>
    /// An unknown outcome retries until the timeout; the last attempt runs when it elapses, and <c>Fail(reason)</c>
    /// dead-letters a message at once. Retry deadlines follow the Durable Jobs clock, which schedules the next wake-up.
    /// An interrupted attempt starts the retry window but dead-letters only a message whose window an earlier failure
    /// started, so one unclean stop never dead-letters a message by itself.
    /// </remarks>
    private string StageFailedAttempt(InboxEntry entry, string reason, DateTimeOffset now, bool interrupted = false)
    {
        var attempts = checked(entry.State.AttemptCount + 1);
        var expirationTime = entry.State.ExpirationTime ?? MessagingTime.AddClamped(now, Options.InboxRetryTimeout);
        if (expirationTime <= now && (!interrupted || entry.State.ExpirationTime is not null))
        {
            StageDeadLetter(entry.Message, reason, attempts);
            return "dead_lettered";
        }

        var retryAt = MessagingTime.AddClamped(now, MessagingTime.GetBackoff(Options.RetryInitialInterval, attempts));
        Emit(new InboxCommand.Retry(entry.Message.Envelope.MessageId, new()
        {
            AttemptCount = attempts,
            NextAttemptAt = retryAt < expirationTime ? retryAt : expirationTime,
            ExpirationTime = expirationTime
        }));
        return "retry";
    }

    private bool IsCurrent(InboxEntry entry) =>
        _messages.TryGetValue(entry.Message.Envelope.MessageId, out var current) && ReferenceEquals(current, entry);

    private void StageCompletion(InboxMessage message, InboxDeadLetterRecord? deadLetter, InboxRejection? rejection = null) =>
        Emit(new InboxCommand.Complete(message.Envelope.MessageId, message.SentAt, deadLetter, rejection));

    private void StageDeadLetter(InboxMessage message, string reason, int attemptCount)
    {
        var now = TimeProvider.GetUtcNow();
        var key = message.Envelope.MessageId;
        CompactDeadLetters(now, reservedCapacity: _deadLetters.ContainsKey(key) ? 0 : 1);
        StageCompletion(message, new InboxDeadLetterRecord
        {
            Message = message,
            DeadLetteredAt = now,
            Reason = reason,
            AttemptCount = attemptCount
        });
    }

    private bool CompactDeadLetters(DateTimeOffset now, int reservedCapacity = 0)
    {
        if (_deadLetters.Count == 0) return false;
        var retained = new Dictionary<MessageId, InboxDeadLetterRecord>(_deadLetters);
        if (!DeadLetterRetention.Compact(retained, now, Options.DeadLetterRetentionPeriod, Options.MaxRetainedDeadLetters,
            static entry => entry.DeadLetteredAt, reservedCapacity))
        {
            return false;
        }

        Emit(new InboxCommand.RemoveDeadLetters(_deadLetters.Keys.Where(key => !retained.ContainsKey(key)).ToArray()));
        return true;
    }

    internal bool RemoveDeadLetter(MessageId messageId)
    {
        _pump.ShutdownToken.ThrowIfCancellationRequested();
        if (!_deadLetters.ContainsKey(messageId)) return false;
        Emit(new InboxCommand.RemoveDeadLetters([messageId]));
        return true;
    }

    /// <summary>
    /// Returns the delay until the next message or registered wake-up is due, or <see langword="null"/> when neither the inbox
    /// nor any wake-up callback reports work which this activation can perform. Messages wait without a wake-up until a handler
    /// is registered.
    /// </summary>
    /// <param name="jobNow">The current Durable Jobs time, which the caller uses to schedule the wake-up.</param>
    internal TimeSpan? GetNextWakeupDelay(DateTimeOffset jobNow)
    {
        var next = GetRegisteredWakeupDelay();
        if (_messages.Count == 0 || _handlers.IsEmpty || next <= TimeSpan.Zero) return next;
        foreach (var entry in _messages.Values)
        {
            // Capture follows synchronous preparation; its wake-up covers the write until acknowledgement.
            if (entry.Sequence > _capturedSequence) continue;
            var delay = GetDelay(entry, jobNow);
            if (delay == TimeSpan.Zero) return delay;
            if (next is null || delay < next) next = delay;
        }

        return next;
    }

    // Callbacks may report any non-positive delay to request a prompt wake-up; it is normalized to zero so that due
    // times derived from it stay representable.
    private TimeSpan? GetRegisteredWakeupDelay()
    {
        TimeSpan? next = null;
        foreach (var getWakeupDelay in _wakeupSources)
        {
            if (getWakeupDelay() is not { } delay) continue;
            if (delay <= TimeSpan.Zero) return TimeSpan.Zero;
            if (next is null || delay < next) next = delay;
        }

        return next;
    }

    // DeliverAt and retry deadlines are in the Durable Jobs time domain, which schedules wake-ups.
    private static TimeSpan GetDelay(InboxEntry entry, DateTimeOffset jobNow)
    {
        var dueTime = GetDueTime(entry);
        return dueTime > jobNow ? dueTime - jobNow : TimeSpan.Zero;
    }

    private static DateTimeOffset GetDueTime(InboxEntry entry)
    {
        var dueTime = entry.Message.Envelope.DeliverAt ?? DateTimeOffset.MinValue;
        return entry.State.NextAttemptAt is { } retryAt && retryAt > dueTime ? retryAt : dueTime;
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var compacted = CompactDeadLetters(TimeProvider.GetUtcNow());
        if (ChargeInterruptedAttempts() | compacted)
        {
            await PersistAsync().ConfigureAwait(true);
        }

        _recovered = true;
    }

    /// <summary>
    /// Charges one failed attempt to each due message unless the previous activation journaled a clean stop, and returns
    /// whether it staged anything.
    /// </summary>
    /// <remarks>
    /// An activation which fails after a handler staged changes, or whose silo crashes, stops without recording the
    /// attempt's outcome. Charging it at recovery keeps backoff and <c>InboxRetryTimeout</c> bounding every failure path
    /// without a journal write on the happy path. A crash also charges due messages which hadn't been dispatched, so
    /// attempts can overcount by one per unclean stop; a charge alone never dead-letters a message whose retry window
    /// hadn't started. The charge commits before the pump runs any handler.
    /// </remarks>
    private bool ChargeInterruptedAttempts()
    {
        if (_messages.Count == 0) return false;
        if (_stoppedCleanly)
        {
            // Consume the marker, so a later unclean stop of this activation is charged.
            Emit(InboxCommand.Resume.Instance);
            return true;
        }

        var now = Shared.JobTimeProvider.GetUtcNow();
        var charged = 0;
        foreach (var entry in _messages.Values.ToArray())
        {
            // A message which isn't due couldn't have been in an attempt.
            if (GetDelay(entry, now) != TimeSpan.Zero) continue;
            var status = StageFailedAttempt(entry, InterruptedAttemptReason, now, interrupted: true);
            Shared.Instruments.OnInboxMessageProcessed(_pump.GrainTypeTag, status);
            charged++;
        }

        if (charged > 0) LogInterruptedAttemptsCharged(Logger, GrainContext.GrainId, charged);
        return charged > 0;
    }

    private const string InterruptedAttemptReason = "The activation stopped without recording the outcome of an attempt.";

    /// <summary>
    /// Journals that this activation stopped with no handler attempt interrupted, so recovery charges none.
    /// </summary>
    /// <remarks>
    /// The pump calls this after its loop ends. A messaging failure, such as a handler which failed after staging
    /// changes, records nothing, so recovery charges the attempt. A failed write leaves the stop unclean, which charges
    /// at most one extra attempt.
    /// </remarks>
    internal async Task RecordCleanStopAsync()
    {
        if (!_recovered || _pump.IsFailed || _stoppedCleanly || _messages.Count == 0) return;
        try
        {
            Emit(InboxCommand.CleanStop.Instance);
            await _pump.StateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            LogCleanStopFailed(Logger, exception, GrainContext.GrainId);
        }
    }

    /// <summary>Releases the reported depth once the activation's messaging state is discarded.</summary>
    internal void ReleaseDepth() => ReportDepth(-_messages.Count);

    private void ReportDepth(int delta) => Shared.Instruments.OnInboxDepthChanged(_pump.GrainTypeTag, delta);

    /// <summary>Persists staged changes while <paramref name="cancellationToken"/> ends only the caller's wait.</summary>
    private ValueTask PersistForCallerAsync(CancellationToken cancellationToken)
    {
        var write = PersistAsync();
        if (write.IsCompleted || !cancellationToken.CanBeCanceled)
        {
            return write;
        }

        // The write continues to its outcome after the caller stops waiting; PersistAsync reports failures through the pump.
        var task = write.AsTask();
        task.Ignore();
        return new(task.WaitAsync(cancellationToken));
    }

    private async ValueTask PersistAsync()
    {
        try
        {
            await _pump.StateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _pump.Fail(exception, "Inbox persistence failed.");
            throw;
        }
    }

    /// <summary>
    /// Tracks one handler attempt and stages its terminal outcome.
    /// </summary>
    internal sealed class HandlerExecution(Inbox owner, InboxEntry entry)
    {
        public bool Active { get; set; }

        /// <summary>Gets or sets the scope which records whether the attempt's own flow staged a change.</summary>
        public StagedChangeScope? StagedChanges { get; set; }

        public bool Completed { get; private set; }

        public string? DeadLetterReason { get; private set; }

        public ExceptionDispatchInfo? Rejection { get; private set; }

        public void Complete()
        {
            ValidateAttempt();
            if (Completed)
            {
                return;
            }

            try
            {
                owner.StageCompletion(entry.Message, deadLetter: null);
                Completed = true;
            }
            catch (Exception exception)
            {
                Reject(exception);
            }
        }

        public void Fail(string reason)
        {
            ValidateAttempt();
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            if (Completed)
            {
                if (string.Equals(DeadLetterReason, reason, StringComparison.Ordinal))
                {
                    return;
                }

                Reject(new InvalidOperationException("The inbox handler has already staged a different terminal outcome."));
            }

            try
            {
                owner.StageDeadLetter(entry.Message, reason, checked(entry.State.AttemptCount + 1));
                DeadLetterReason = reason;
                Completed = true;
            }
            catch (Exception exception)
            {
                Reject(exception);
            }
        }

        public void ThrowIfRejected() => Rejection?.Throw();

        [DoesNotReturn]
        public void Reject(Exception exception)
        {
            Rejection ??= ExceptionDispatchInfo.Capture(exception);
            Rejection.Throw();
        }

        private void ValidateAttempt()
        {
            ThrowIfRejected();
            if (!Active)
            {
                Reject(new InvalidOperationException("The inbox handler context belongs to an inactive or different attempt."));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Inbox command staging failed.")]
    private static partial void LogStagingFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox for {ReceiverId} charged an interrupted attempt to {Count} due messages: its previous activation stopped without recording their outcome.")]
    private static partial void LogInterruptedAttemptsCharged(ILogger logger, GrainId receiverId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox for {ReceiverId} couldn't record a clean stop, so its next activation charges one attempt to each due message.")]
    private static partial void LogCleanStopFailed(ILogger logger, Exception exception, GrainId receiverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox for {ReceiverId} expired message {MessageId}: its send time {SentAt} is at or below the durable admission floor {AdmissionFloor}. Its earlier delivery outcome is unknown.")]
    private static partial void LogMessageExpired(ILogger logger, MessageId messageId, GrainId receiverId, DateTimeOffset sentAt, DateTimeOffset admissionFloor);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox for {ReceiverId} rejected message {MessageId}: its send time {SentAt} is later than receiver time {ReceiverTime} plus DeduplicationWindow.")]
    private static partial void LogSentAtTooFarAhead(ILogger logger, MessageId messageId, GrainId receiverId, DateTimeOffset sentAt, DateTimeOffset receiverTime);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No inbox handler registered for message {MessageId} to {ReceiverId}")]
    private static partial void LogHandlerNotFound(ILogger logger, MessageId messageId, GrainId receiverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Accepted message {MessageId} to {ReceiverId}")]
    private static partial void LogMessageAccepted(ILogger logger, MessageId messageId, GrainId receiverId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handler threw exception for message {MessageId} to {ReceiverId}")]
    private static partial void LogHandlerException(ILogger logger, Exception exception, MessageId messageId, GrainId receiverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox message sink rejected message {MessageId} to {ReceiverId} with code {Code}: {Reason}")]
    private static partial void LogSinkRejected(ILogger logger, MessageId messageId, GrainId receiverId, string reason, string? code);
}

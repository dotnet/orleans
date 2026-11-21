using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Durable.Messaging.Configuration;
using Orleans.Durable.Messaging.Diagnostics;
using Orleans.Durable.Journaling;
using Orleans.Runtime;

namespace Orleans.Durable.Messaging;

/// <summary>
/// Delivers journal-acknowledged messages to their receivers' inboxes.
/// </summary>
internal sealed partial class Outbox : IStateMachine, IReportsStagedChanges, IDurableValueCommandHandler<OutboxCommand>, IOutbox
{
    private readonly MessagingPump _pump;
    private readonly Inbox _inbox;
    private JournalStreamWriter _writer;
    private readonly Dictionary<MessageId, OutboxEntry> _messages = [];
    private readonly Dictionary<MessageId, OutboxDeadLetterRecord> _deadLetters = [];
    private readonly List<IOutboxMessageSource> _sources = [];

    // Entries whose delivery attempt's outcome is not applied yet, the outcomes of completed attempts, and the number of
    // attempts still running. An entry stays in flight until its outcome is applied, so it never has two attempts.
    private readonly HashSet<OutboxEntry> _inFlight = new(ReferenceEqualityComparer.Instance);
    private readonly List<DeliveryOutcome> _outcomes = [];
    private int _running;
    private TaskCompletionSource? _drained;

    // A grain's messages to itself go to its own inbox, so resolving the outbox resolves the inbox.
    public Outbox(MessagingPump pump, Inbox inbox)
    {
        _pump = pump;
        _inbox = inbox;
        pump.StateManager.RegisterStateMachine(MessagingStateNames.Outbox, this);
        pump.Register(this);
    }

    private MessagingShared Shared => _pump.Shared;
    private DurableMessagingOptions Options => _pump.Shared.Options;
    private ILogger Logger => _pump.Shared.OutboxLogger;
    private TimeProvider TimeProvider => Shared.TimeProvider;
    private TimeProvider JobTimeProvider => Shared.JobTimeProvider;
    private GrainId GrainId => _pump.GrainContext.GrainId;

    internal IReadOnlyDictionary<MessageId, OutboxEntry> MessageState => _messages;
    internal IReadOnlyDictionary<MessageId, OutboxDeadLetterRecord> DeadLetterState => _deadLetters;
    internal int Count => _messages.Count;

    /// <summary>Gets whether delivery attempts are in flight or awaiting application by the pump.</summary>
    internal bool IsDelivering => _inFlight.Count > 0;

    public IOutbox Register(IOutboxMessageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _sources.Add(source);
        return this;
    }

    public void Send(OutboxMessage message)
    {
        _pump.ShutdownToken.ThrowIfCancellationRequested();
        EnvelopeValidation.Validate(message);
        if (_messages.TryGetValue(message.Envelope.MessageId, out var existing))
        {
            if (existing.Message is not { } pending || !EnvelopeEquivalence.AreEquivalent(pending, message))
            {
                throw new InvalidOperationException(
                    $"The outbox already contains a different envelope with message ID '{message.Envelope.MessageId}'.");
            }
            return;
        }

        if (message.ReceiverId == GrainId)
        {
            // The grain's own inbox accepts the message in the caller's journal write, so the outbox never holds it.
            _inbox.StageLocal(message.ToInboxMessage(JobTimeProvider.GetUtcNow()));
            Shared.Instruments.OnOutboxMessageSent(_pump.GrainTypeTag);
            return;
        }

        try
        {
            Emit(new OutboxCommand.Enqueue(message, CreateState()));
            Shared.Instruments.OnOutboxMessageSent(_pump.GrainTypeTag);
        }
        catch (Exception exception)
        {
            FailStaging(exception);
        }
    }

    public void Send(MessageId messageId)
    {
        _pump.ShutdownToken.ThrowIfCancellationRequested();
        EnvelopeValidation.ValidateMessageId(messageId);
        if (_messages.TryGetValue(messageId, out var existing))
        {
            // The source is authoritative for a pending reference entry's message, so a repeated send coalesces.
            if (existing.Reference is null)
            {
                throw new InvalidOperationException(
                    $"The outbox already contains a different message with message ID '{messageId}'.");
            }
            return;
        }

        if (FindSource(messageId, out _) is not { } source)
        {
            throw new InvalidOperationException($"No registered outbox message source provides message '{messageId}'.");
        }

        try
        {
            Emit(new OutboxCommand.EnqueueReference(new() { MessageId = messageId }, CreateState()));
            _messages[messageId].Source = source;
            Shared.Instruments.OnOutboxMessageSent(_pump.GrainTypeTag);
        }
        catch (Exception exception)
        {
            FailStaging(exception);
        }
    }

    public bool Withdraw(MessageId messageId)
    {
        _pump.ShutdownToken.ThrowIfCancellationRequested();
        if (!_messages.TryGetValue(messageId, out var entry)) return false;
        if (entry.Reference is null)
        {
            throw new InvalidOperationException($"Message '{messageId}' is pending as an ordinary outbox message, which can't be withdrawn.");
        }

        try
        {
            Emit(new OutboxCommand.Retire(messageId));
        }
        catch (Exception exception)
        {
            FailStaging(exception);
        }

        Shared.Instruments.OnOutboxMessageDelivered(_pump.GrainTypeTag, "withdrawn");
        LogMessageWithdrawn(Logger, messageId);
        return true;
    }

    // The sending write fixes the logical send time for every attempt and recovery.
    private OutboxMessageState CreateState() =>
        new() { SentAt = JobTimeProvider.GetUtcNow() };

    private DateTimeOffset GetExpirationTime(OutboxMessageState state) =>
        MessagingTime.AddClamped(state.SentAt!.Value, Options.OutboxRetryTimeout);

    /// <summary>Returns the first registered source which provides the message, and its validated message.</summary>
    private IOutboxMessageSource? FindSource(MessageId messageId, out OutboxMessage message)
    {
        foreach (var source in _sources)
        {
            if (TryGetSourceMessage(source, messageId, out message)) return source;
        }

        message = default;
        return null;
    }

    /// <summary>
    /// Returns the entry's message, materializing a reference entry's message from its owning source, or returns
    /// <see langword="false"/> for a recovered entry which no registered source provides. Throws when the owning source
    /// throws, returns an invalid message, or no longer provides the message.
    /// </summary>
    private bool TryGetMessage(OutboxEntry entry, out OutboxMessage message)
    {
        if (entry.Message is { } ordinary)
        {
            message = ordinary;
            return true;
        }

        if (entry.Source is { } owner)
        {
            if (TryGetSourceMessage(owner, entry.MessageId, out message)) return true;

            // The owner provided the message when it was sent, and withdrawal goes through IOutbox.Withdraw.
            throw new InvalidOperationException(
                $"The outbox message source which owns message '{entry.MessageId}' no longer provides it; withdraw a message with IOutbox.Withdraw.");
        }

        // A recovered entry's owner is the first source which doesn't decline it, including one which fails to provide
        // it, so that source is notified when the entry is dead-lettered.
        foreach (var source in _sources)
        {
            entry.Source = source;
            if (TryGetSourceMessage(source, entry.MessageId, out message)) return true;
        }

        entry.Source = null;
        message = default;
        return false;
    }

    private bool TryGetSourceMessage(IOutboxMessageSource source, MessageId messageId, out OutboxMessage message)
    {
        if (!source.TryGetMessage(messageId, out message)) return false;
        EnvelopeValidation.Validate(message);
        if (message.Envelope.MessageId != messageId)
        {
            throw new InvalidOperationException(
                $"The outbox message source returned message ID '{message.Envelope.MessageId}' for message '{messageId}'.");
        }
        if (message.ReceiverId == GrainId)
        {
            // The grain's own inbox would hold the payload, so a source cannot keep it as the single copy.
            throw new ArgumentException(
                $"The outbox message source addressed message '{messageId}' to the sending grain; send its message with Send(OutboxMessage) instead.",
                nameof(messageId));
        }

        return true;
    }

    /// <summary>Notifies the source of a retired reference entry, so its release commits with the entry's removal.</summary>
    private void Retire(OutboxEntry entry, OutboxMessageRetirement retirement)
    {
        // A recovered reference which no source provides has no owner to notify.
        entry.Source?.OnRetired(entry.MessageId, retirement);
        MessagingEvents.EmitOutboxMessageRetired(_pump.GrainContext, entry.MessageId, retirement);
    }

    /// <summary>
    /// Creates the message of a dead letter whose source failed to provide it: the message ID with an empty payload and
    /// a default receiver, which no valid outgoing message has.
    /// </summary>
    private static OutboxMessage CreateUnmaterializedMessage(MessageId messageId) =>
        new() { Envelope = new() { MessageId = messageId, Payload = [] }, ReceiverId = default };

    /// <summary>Copies the payload and header values of a message which a source owns.</summary>
    private static OutboxMessage CopySourceMessage(OutboxMessage message)
    {
        var envelope = message.Envelope;
        Dictionary<string, byte[]>? headers = null;
        if (envelope.Headers is { } sourceHeaders)
        {
            headers = new(sourceHeaders.Count, sourceHeaders.Comparer);
            foreach (var (name, value) in sourceHeaders) headers.Add(name, value.AsSpan().ToArray());
        }

        return message with { Envelope = envelope with { Payload = envelope.Payload.AsSpan().ToArray(), Headers = headers } };
    }

    private void OnRecoveryCompleted()
    {
        if (_sources.Count == 0 && _messages.Values.Any(static entry => entry.Reference is not null))
        {
            throw new InvalidOperationException(
                "The outbox recovered messages which a source provides, but no outbox message source is registered. Register outbox message sources during activation setup, before journal recovery.");
        }

        // Sending validated every message, and replay keys each entry by its own message ID.
        Debug.Assert(_messages.All(pair => pair.Key == pair.Value.MessageId
            && (pair.Value.Message is null) != (pair.Value.Reference is null)
            && pair.Value.Message?.ReceiverId != GrainId));
        Debug.Assert(_deadLetters.All(static pair => pair.Key == pair.Value.Message.Envelope.MessageId));
        _capturedSequence = _durableSequence = _lastSequence;
    }

    private void ResetState()
    {
        ReportDepth(-_messages.Count);
        _messages.Clear();
        _deadLetters.Clear();
        _lastSequence = _capturedSequence = _durableSequence = 0;
    }

    [DoesNotReturn]
    private void FailStaging(Exception exception)
    {
        LogStagingFailed(Logger, exception);
        _pump.Fail(exception, "Outbox staging failed.");
        ExceptionDispatchInfo.Throw(exception);
    }

    private async ValueTask PersistAsync()
    {
        try
        {
            await _pump.StateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _pump.Fail(exception, "Outbox persistence failed.");
            throw;
        }
    }

    /// <summary>
    /// Selects up to <paramref name="capacity"/> durable entries which are due and have no attempt in flight: the
    /// messages to attempt, and the entries to dead-letter without an attempt.
    /// </summary>
    private (List<(OutboxEntry Entry, OutboxMessage Message)>? Candidates, List<(OutboxEntry Entry, Unavailable Reason)>? Unavailable) SelectMessages(int capacity)
    {
        var now = JobTimeProvider.GetUtcNow();
        List<(OutboxEntry Entry, OutboxMessage Message)>? candidates = null;
        List<(OutboxEntry Entry, Unavailable Reason)>? unavailable = null;
        var selected = 0;
        foreach (var entry in _messages.Values)
        {
            if (entry.Sequence > _durableSequence || _inFlight.Contains(entry) || entry.State.NextAttemptAt > now) continue;

            // Entries which are only removed count toward the capacity too, so a pass removes at most a batch of entries.
            if (++selected > capacity) break;
            OutboxMessage? message = null;
            Exception? error = null;
            try
            {
                if (TryGetMessage(entry, out var provided)) message = provided;
            }
            catch (Exception exception)
            {
                // The entry is durable, so a source which can't provide a valid message dead-letters only that entry.
                error = exception;
            }

            // Backpressure and failed attempts share the absolute retry budget established by the sending write.
            if (GetExpirationTime(entry.State) <= now) (unavailable ??= []).Add((entry, new Unavailable.Expired(message)));
            else if (error is not null) (unavailable ??= []).Add((entry, new Unavailable.Failed(error)));
            else if (message is { } candidate) (candidates ??= []).Add((entry, candidate));
            else (unavailable ??= []).Add((entry, Unavailable.Unclaimed.Instance));
        }

        if (candidates is null) LogNoMessages(Logger, Count);
        else LogDeliveringMessages(Logger, candidates.Count);
        return (candidates, unavailable);
    }

    private async Task<DeliveryOutcome> DeliverAsync(OutboxEntry entry, OutboxMessage message, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            // Retention and admission use the original send time, including for delayed attempts and recovered entries.
            var delivery = new InboxMessage
            {
                Envelope = message.Envelope,
                SentAt = entry.State.SentAt!.Value
            };
            var result = await Shared.Transport.DeliverAsync(message.ReceiverId, delivery, cancellationToken).ConfigureAwait(true);
            return new(entry, message, new DeliveryAnswer.Answered(result), Stopwatch.GetElapsedTime(start));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(entry, message, new DeliveryAnswer.Failed("Delivery was canceled."), Stopwatch.GetElapsedTime(start));
        }
        catch (Exception exception)
        {
            if (Logger.IsEnabled(LogLevel.Error))
            {
                LogDeliveryError(Logger, exception, message.Envelope.MessageId, message.ReceiverId);
            }
            return new(entry, message, new DeliveryAnswer.Failed(exception.ToString()), Stopwatch.GetElapsedTime(start));
        }
    }

    /// <summary>
    /// Applies completed delivery attempts and starts attempts for due messages, keeping up to
    /// <see cref="DurableMessagingOptions.OutboxBatchSize"/> attempts in flight, and returns whether it changed state.
    /// </summary>
    /// <remarks>
    /// Each attempt completes on its own, so a slow receiver holds one slot without delaying messages to other receivers.
    /// </remarks>
    internal async ValueTask<bool> AdvanceAsync(CancellationToken cancellationToken)
    {
        var progress = false;
        if (_outcomes.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcomes = _outcomes.ToArray();
            _outcomes.Clear();
            foreach (var outcome in outcomes) _inFlight.Remove(outcome.Entry);
            await ApplyDeliveriesAsync(outcomes).ConfigureAwait(true);
            progress = true;
        }

        var capacity = Options.OutboxBatchSize - _inFlight.Count;
        if (capacity <= 0) return progress;
        var (candidates, unavailable) = SelectMessages(capacity);

        // Attempts start before any await, so no withdrawal or source change interleaves between materializing a message
        // and calling its receiver.
        if (candidates is not null)
        {
            foreach (var (entry, message) in candidates) RunAttemptAsync(entry, message).Ignore();
        }

        if (unavailable is not null)
        {
            await DeadLetterUnavailableAsync(unavailable).ConfigureAwait(true);
            progress = true;
        }

        return progress;
    }

    private async Task RunAttemptAsync(OutboxEntry entry, OutboxMessage message)
    {
        _inFlight.Add(entry);
        _running++;
        var outcome = await DeliverAsync(entry, message, _pump.ShutdownToken).ConfigureAwait(true);
        _outcomes.Add(outcome);
        if (--_running == 0 && _drained is { } drained)
        {
            _drained = null;
            drained.TrySetResult();
        }

        _pump.Signal();
    }

    private async ValueTask ApplyDeliveriesAsync(DeliveryOutcome[] outcomes)
    {
        int applied = 0, delivered = 0, backpressured = 0;
        try
        {
            foreach (var outcome in outcomes)
            {
                // An entry which was withdrawn, or removed and sent again, since its attempt started has a stale outcome.
                var id = outcome.Entry.MessageId;
                if (!_messages.TryGetValue(id, out var current) || !ReferenceEquals(current, outcome.Entry)) continue;

                switch (outcome.Answer)
                {
                    case DeliveryAnswer.Answered { Result: DeliveryResult.Expired }:
                        // Expired means this send is at or below the admission floor; earlier acceptance remains unknown.
                        StageDeadLetter(
                            current,
                            current.Reference is null ? outcome.Message : CopySourceMessage(outcome.Message),
                            "The message was sent before the receiver's deduplication window; its earlier delivery outcome is unknown.",
                            checked(current.State.AttemptCount + 1));
                        break;
                    case DeliveryAnswer.Answered { Result: DeliveryResult.Accepted or DeliveryResult.Duplicate }:
                        Emit(new OutboxCommand.Retire(id));
                        Retire(current, OutboxMessageRetirement.Delivered.Instance);
                        delivered++;
                        break;
                    case DeliveryAnswer.Answered { Result: DeliveryResult.DeadLettered { Reason: var reason, Code: var code } }:
                        Emit(new OutboxCommand.Retire(id));
                        Retire(current, new OutboxMessageRetirement.Rejected(reason, code));
                        delivered++;
                        break;
                    default:
                        // Backpressure, a missing handler, and a failed call leave the outcome unknown, so the message retries.
                        var attemptCount = checked(current.State.AttemptCount + 1);
                        var error = outcome.Answer switch
                        {
                            DeliveryAnswer.Failed { Error: var failure } => failure,
                            DeliveryAnswer.Answered { Result: DeliveryResult.Backpressured } => "The receiver is backpressured.",
                            _ => "The receiver has no registered inbox handler."
                        };
                        if (outcome.Answer is DeliveryAnswer.Answered { Result: DeliveryResult.Backpressured }) backpressured++;
                        if (PrepareRetry(outcome, attemptCount, error) is { } retry)
                        {
                            Emit(new OutboxCommand.Retry(id, retry));
                            MessagingEvents.EmitOutboxRetryScheduled(
                                _pump.GrainContext, id, outcome.Message.ReceiverId, attemptCount, retry.NextAttemptAt!.Value, error);
                        }
                        else
                        {
                            // The source may release a reference entry's payload and headers when it retires, so the dead letter keeps a copy.
                            StageDeadLetter(current, current.Reference is null ? outcome.Message : CopySourceMessage(outcome.Message), error, attemptCount);
                        }
                        break;
                }

                RecordDeliveryMetrics(outcome);
                applied++;
            }
        }
        catch (Exception exception)
        {
            FailStaging(exception);
        }

        if (applied == 0) return;
        await PersistAsync().ConfigureAwait(true);
        LogDeliveryComplete(Logger, delivered, backpressured, applied - delivered - backpressured, Count);
    }

    /// <summary>
    /// Returns the retry state after a failed attempt, or <see langword="null"/> when the retry timeout has elapsed.
    /// </summary>
    private OutboxMessageState? PrepareRetry(DeliveryOutcome outcome, int attemptCount, string error)
    {
        var now = JobTimeProvider.GetUtcNow();
        var expirationTime = GetExpirationTime(outcome.Entry.State);
        if (expirationTime <= now) return null;

        // The next attempt runs no later than the timeout, when the outbox dead-letters the message instead.
        var retryAt = MessagingTime.AddClamped(now, MessagingTime.GetBackoff(Options.RetryInitialInterval, attemptCount));
        return new OutboxMessageState
        {
            AttemptCount = attemptCount,
            LastError = error,
            SentAt = outcome.Entry.State.SentAt,
            NextAttemptAt = retryAt < expirationTime ? retryAt : expirationTime
        };
    }

    /// <summary>
    /// Dead-letters entries without an attempt: those whose retry timeout elapsed, those whose owning sources failed to
    /// provide a valid message, and recovered references which no registered source provides.
    /// </summary>
    private async ValueTask DeadLetterUnavailableAsync(List<(OutboxEntry Entry, Unavailable Reason)> unavailable)
    {
        try
        {
            foreach (var (entry, reason) in unavailable)
            {
                // A retirement callback earlier in this loop may have withdrawn the entry, which then has no outcome.
                var id = entry.MessageId;
                if (!_messages.TryGetValue(id, out var current) || !ReferenceEquals(current, entry)) continue;

                switch (reason)
                {
                    case Unavailable.Expired { Message: var message }:
                        LogMessageExpired(Logger, id);
                        StageDeadLetter(
                            entry,
                            message is not { } expired ? CreateUnmaterializedMessage(id) : entry.Reference is null ? expired : CopySourceMessage(expired),
                            entry.State.LastError is { } last
                                ? $"The outbox retry timeout elapsed after the last attempt failed: {last}"
                                : "The outbox retry timeout elapsed before the message was delivered.",
                            entry.State.AttemptCount);
                        Shared.Instruments.OnOutboxMessageDelivered(_pump.GrainTypeTag, "expired");
                        break;
                    case Unavailable.Failed { Error: var error }:
                        LogSourceFailed(Logger, error, id);
                        StageDeadLetter(entry, CreateUnmaterializedMessage(id),
                            $"The outbox message source failed to provide the message: {error}", checked(entry.State.AttemptCount + 1));
                        Shared.Instruments.OnOutboxMessageDelivered(_pump.GrainTypeTag, "source_error");
                        break;
                    case Unavailable.Unclaimed:
                        LogMessageUnprovided(Logger, id);
                        StageDeadLetter(entry, CreateUnmaterializedMessage(id),
                            "No registered outbox message source provides the message.", entry.State.AttemptCount);
                        Shared.Instruments.OnOutboxMessageDelivered(_pump.GrainTypeTag, "unprovided");
                        break;
                }
            }
        }
        catch (Exception exception)
        {
            FailStaging(exception);
        }

        await PersistAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Stages the removal of <paramref name="entry"/> with its dead letter, and retires it with its owning source, if any,
    /// in the same journal write.
    /// </summary>
    private void StageDeadLetter(
        OutboxEntry entry, OutboxMessage message, string reason, int attemptCount, OutboxMessageRetirement? retirement = null)
    {
        var now = TimeProvider.GetUtcNow();
        var id = entry.MessageId;
        CompactDeadLetters(now, reservedCapacity: _deadLetters.ContainsKey(id) ? 0 : 1);
        Emit(new OutboxCommand.Retire(id, new() { Message = message, DeadLetteredAt = now, Reason = reason, AttemptCount = attemptCount }));
        Retire(entry, retirement ?? new OutboxMessageRetirement.DeadLettered(reason));
    }

    private bool CompactDeadLetters(DateTimeOffset now, int reservedCapacity = 0)
    {
        if (_deadLetters.Count == 0) return false;
        var retained = new Dictionary<MessageId, OutboxDeadLetterRecord>(_deadLetters);
        if (!DeadLetterRetention.Compact(retained, now, Options.DeadLetterRetentionPeriod, Options.MaxRetainedDeadLetters,
            static entry => entry.DeadLetteredAt, reservedCapacity))
        {
            return false;
        }

        Emit(new OutboxCommand.RemoveDeadLetters(_deadLetters.Keys.Where(key => !retained.ContainsKey(key)).ToArray()));
        return true;
    }
    private void RecordDeliveryMetrics(DeliveryOutcome outcome)
    {
        var message = outcome.Message;
        if (outcome.Answer is not DeliveryAnswer.Answered { Result: var result })
        {
            Shared.Instruments.OnOutboxMessageDelivered(_pump.GrainTypeTag, "error");
            Shared.Instruments.OnOutboxDeliveryDuration(outcome.Duration, _pump.GrainTypeTag);
            return;
        }

        var metricStatus = result switch
        {
            DeliveryResult.Accepted => "accepted",
            DeliveryResult.Duplicate => "duplicate",
            DeliveryResult.DeadLettered => "deadlettered",
            DeliveryResult.Backpressured => "backpressured",
            DeliveryResult.HandlerNotFound => "handler_not_found",
            DeliveryResult.Expired => "expired"
        };
        Shared.Instruments.OnOutboxMessageDelivered(_pump.GrainTypeTag, metricStatus);
        Shared.Instruments.OnOutboxDeliveryDuration(outcome.Duration, _pump.GrainTypeTag);
        switch (result)
        {
            case DeliveryResult.Accepted or DeliveryResult.Duplicate or DeliveryResult.DeadLettered or DeliveryResult.Expired:
                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    LogMessageDelivered(Logger, message.Envelope.MessageId, message.ReceiverId, metricStatus);
                }
                break;
            case DeliveryResult.Backpressured:
                LogDeliveryBackpressured(Logger, message.Envelope.MessageId, message.ReceiverId);
                break;
            case DeliveryResult.HandlerNotFound:
                LogDeliveryHandlerNotFound(Logger, message.Envelope.MessageId, message.ReceiverId);
                break;
        }
    }

    /// <summary>
    /// Returns the delay until the next captured delivery attempt is due, or <see langword="null"/> when none remain.
    /// </summary>
    /// <param name="now">The current Durable Jobs time.</param>
    internal TimeSpan? GetNextWakeupDelay(DateTimeOffset now)
    {
        TimeSpan? next = null;
        foreach (var entry in _messages.Values)
        {
            // Capture follows synchronous preparation; its wake-up covers the write until acknowledgement.
            if (entry.Sequence > _capturedSequence) continue;
            var at = entry.State.NextAttemptAt ?? now;
            var delay = at > now ? at - now : TimeSpan.Zero;
            if (delay == TimeSpan.Zero) return delay;
            if (next is null || delay < next) next = delay;
        }
        return next;
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!CompactDeadLetters(TimeProvider.GetUtcNow())) return;
        }
        catch (Exception exception)
        {
            FailStaging(exception);
        }

        await PersistAsync().ConfigureAwait(true);
    }

    /// <summary>Waits for in-flight deliveries, which observe the pump's shutdown token.</summary>
    internal Task StopAsync() => _running == 0 ? Task.CompletedTask : (_drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    /// <summary>Releases the reported depth once the activation's messaging state is discarded.</summary>
    internal void ReleaseDepth() => ReportDepth(-_messages.Count);

    private void ReportDepth(int delta) => Shared.Instruments.OnOutboxDepthChanged(_pump.GrainTypeTag, delta);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox command staging failed.")]
    private static partial void LogStagingFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No durable messages to deliver (all {Count} messages are still pending)")]
    private static partial void LogNoMessages(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Delivering {Count} durable messages from outbox")]
    private static partial void LogDeliveringMessages(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Delivered message {MessageId} to {ReceiverId} (Status: {Status})")]
    private static partial void LogMessageDelivered(ILogger logger, MessageId messageId, GrainId receiverId, string status);

    [LoggerMessage(Level = LogLevel.Error, Message = "No inbox handler for message {MessageId} to {ReceiverId}")]
    private static partial void LogDeliveryHandlerNotFound(ILogger logger, MessageId messageId, GrainId receiverId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Backpressured delivering message {MessageId} to {ReceiverId}, will retry later")]
    private static partial void LogDeliveryBackpressured(ILogger logger, MessageId messageId, GrainId receiverId);


    [LoggerMessage(Level = LogLevel.Error, Message = "Error delivering message {MessageId} to {ReceiverId}")]
    private static partial void LogDeliveryError(ILogger logger, Exception exception, MessageId messageId, GrainId receiverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed outbox message {MessageId}, which its source withdrew")]
    private static partial void LogMessageWithdrawn(ILogger logger, MessageId messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dead-lettered recovered outbox message {MessageId}, which no registered outbox message source provides")]
    private static partial void LogMessageUnprovided(ILogger logger, MessageId messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dead-lettered outbox message {MessageId}, whose retry timeout elapsed")]
    private static partial void LogMessageExpired(ILogger logger, MessageId messageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The outbox message source failed to provide message {MessageId}; the message is dead-lettered")]
    private static partial void LogSourceFailed(ILogger logger, Exception exception, MessageId messageId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Outbox delivery complete: {DeliveredCount} delivered, {BackpressuredCount} backpressured, {FailedCount} failed, {RemainingCount} remaining")]
    private static partial void LogDeliveryComplete(ILogger logger, int deliveredCount, int backpressuredCount, int failedCount, int remainingCount);

    private readonly record struct DeliveryOutcome(OutboxEntry Entry, OutboxMessage Message, DeliveryAnswer Answer, TimeSpan Duration);

    /// <summary>The receiver's answer to a delivery attempt, or the failure which left the attempt's outcome unknown.</summary>
    private union DeliveryAnswer(DeliveryAnswer.Answered, DeliveryAnswer.Failed)
    {
        public sealed class Answered(DeliveryResult result)
        {
            public DeliveryResult Result { get; } = result;
        }

        public sealed class Failed(string error)
        {
            public string Error { get; } = error;
        }
    }

    /// <summary>Why a due entry is dead-lettered without an attempt.</summary>
    private union Unavailable(Unavailable.Expired, Unavailable.Failed, Unavailable.Unclaimed)
    {
        /// <summary>The retry timeout elapsed; the source's message, when it provided one, is kept in the dead letter.</summary>
        public sealed class Expired(OutboxMessage? message)
        {
            public OutboxMessage? Message { get; } = message;
        }

        /// <summary>The owning source failed to provide a valid message.</summary>
        public sealed class Failed(Exception error)
        {
            public Exception Error { get; } = error;
        }

        /// <summary>No registered source provides the recovered reference.</summary>
        public sealed class Unclaimed
        {
            public static Unclaimed Instance { get; } = new();
        }
    }
}

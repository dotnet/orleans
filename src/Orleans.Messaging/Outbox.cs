using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Messaging.Configuration;
using Orleans.Messaging.Diagnostics;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.Messaging;

/// <summary>
/// Delivers journal-acknowledged messages to their receivers' inboxes.
/// </summary>
internal sealed partial class Outbox : IStateMachine, IReportsStagedChanges, IDurableValueCommandHandler<OutboxCommand>, IOutbox
{
    private readonly MessagingPump _pump;
    private JournalStreamWriter _writer;
    private readonly Dictionary<MessageId, OutboxEntry> _messages = [];
    private readonly Dictionary<MessageId, OutboxDeadLetter> _deadLetters = [];
    private readonly List<IOutboxMessageSource> _sources = [];

    // Entries whose delivery attempt's outcome is not applied yet, the outcomes of completed attempts, and the number of
    // attempts still running. An entry stays in flight until its outcome is applied, so it never has two attempts.
    private readonly HashSet<OutboxEntry> _inFlight = new(ReferenceEqualityComparer.Instance);
    private readonly List<DeliveryOutcome> _outcomes = [];
    private int _running;
    private TaskCompletionSource? _drained;
    private readonly JournaledStateAccessTarget _accessTarget;

    public Outbox(MessagingPump pump)
    {
        _pump = pump;
        _accessTarget = new(MessagingStateNames.Outbox, pump.StateManager);
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
    internal IReadOnlyDictionary<MessageId, OutboxDeadLetter> DeadLetterState => _deadLetters;
    internal int Count => _messages.Count;

    /// <summary>Gets whether delivery attempts are in flight or awaiting application by the pump.</summary>
    internal bool IsDelivering => _inFlight.Count > 0;

    public IOutbox Register(IOutboxMessageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _sources.Add(source);
        return this;
    }

    /// <remarks>
    /// A send runs through the caller's <see cref="JournaledStateContext"/> as a write, so a durable task body's replay
    /// doesn't send again.
    /// </remarks>
    public void Send(OutboxMessage message)
    {
        // Shutdown fails a send before it runs, so no context records the shutdown as the send's outcome.
        _pump.ShutdownToken.ThrowIfCancellationRequested();
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            context.Write(_accessTarget, JournaledStateOperation.OutboxSend, message.Envelope.MessageId, (Self: this, Message: message), static state => state.Self.SendCore(state.Message));
            return;
        }

        SendCore(message);
    }

    private void SendCore(OutboxMessage message)
    {
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
            _pump.Inbox.StageLocal(message.ToInboxMessage(JobTimeProvider.GetUtcNow()));
            Shared.Instruments.OnOutboxMessageSent(Shared.GrainType);
            return;
        }

        try
        {
            Emit(new() { Kind = OutboxCommandKind.Enqueue, Message = message, State = CreateState() });
            Shared.Instruments.OnOutboxMessageSent(Shared.GrainType);
        }
        catch (Exception exception)
        {
            FailStaging(exception);
        }
    }

    /// <remarks>A send runs through the caller's <see cref="JournaledStateContext"/> as a write.</remarks>
    public void Send(MessageId messageId)
    {
        _pump.ShutdownToken.ThrowIfCancellationRequested();
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            context.Write(_accessTarget, JournaledStateOperation.OutboxSend, messageId, (Self: this, MessageId: messageId), static state => state.Self.SendCore(state.MessageId));
            return;
        }

        SendCore(messageId);
    }

    private void SendCore(MessageId messageId)
    {
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
            Emit(new()
            {
                Kind = OutboxCommandKind.Enqueue,
                Reference = new() { MessageId = messageId },
                State = CreateState()
            });
            _messages[messageId].Source = source;
            Shared.Instruments.OnOutboxMessageSent(Shared.GrainType);
        }
        catch (Exception exception)
        {
            FailStaging(exception);
        }
    }

    /// <remarks>A withdrawal runs through the caller's <see cref="JournaledStateContext"/> as a write.</remarks>
    public bool Withdraw(MessageId messageId) => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Write(_accessTarget, JournaledStateOperation.OutboxWithdraw, messageId, (Self: this, MessageId: messageId), static state => state.Self.WithdrawCore(state.MessageId))
        : WithdrawCore(messageId);

    private bool WithdrawCore(MessageId messageId)
    {
        _pump.ShutdownToken.ThrowIfCancellationRequested();
        if (!_messages.TryGetValue(messageId, out var entry)) return false;
        if (entry.Reference is null)
        {
            throw new InvalidOperationException($"Message '{messageId}' is pending as an ordinary outbox message, which can't be withdrawn.");
        }

        try
        {
            Emit(new() { Kind = OutboxCommandKind.Delivery, MessageId = messageId });
        }
        catch (Exception exception)
        {
            FailStaging(exception);
        }

        Shared.Instruments.OnOutboxMessageDelivered(Shared.GrainType, "withdrawn");
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

    private static bool IsUnmaterialized(OutboxMessage message) =>
        message.ReceiverId.IsDefault
        && message.Envelope is { Payload.Length: 0, Subject: null, Headers: null, DeliverAt: null };

    private void OnRecoveryCompleted()
    {
        if (_sources.Count == 0 && _messages.Values.Any(static entry => entry.Reference is not null))
        {
            throw new InvalidOperationException(
                "The outbox recovered messages which a source provides, but no outbox message source is registered. Register outbox message sources during activation setup, before journal recovery.");
        }

        foreach (var (key, entry) in _messages)
        {
            if (entry.State.SentAt is null)
            {
                throw new InvalidOperationException("A recovered outbox entry requires its send time.");
            }

            if (entry.Message is { } message)
            {
                if (entry.Reference is not null)
                {
                    throw new InvalidOperationException("A recovered outbox entry has both a message and a message reference.");
                }

                EnvelopeValidation.Validate(message);
            }
            else
            {
                EnvelopeValidation.ValidateMessageId(key);
            }

            if (key != entry.MessageId)
            {
                throw new InvalidOperationException("The recovered outbox key does not match its command identity.");
            }
            if (entry.Message?.ReceiverId == GrainId)
            {
                throw new InvalidOperationException("A recovered outbox message is addressed to its own grain, whose inbox accepts such messages directly.");
            }
        }
        foreach (var (key, letter) in _deadLetters)
        {
            if (IsUnmaterialized(letter.Message))
            {
                EnvelopeValidation.ValidateMessageId(letter.Message.Envelope.MessageId);
            }
            else
            {
                EnvelopeValidation.Validate(letter.Message);
            }

            if (key != letter.Message.Envelope.MessageId)
            {
                throw new InvalidOperationException("The recovered outbox dead-letter key does not match its message identity.");
            }
            if (letter.Message.ReceiverId == GrainId)
            {
                throw new InvalidOperationException("A recovered outbox dead letter is addressed to its own grain, whose inbox accepts such messages directly.");
            }
        }
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
    private (List<(OutboxEntry Entry, OutboxMessage Message)>? Candidates, List<Unavailable>? Unavailable) SelectMessages(int capacity)
    {
        var now = JobTimeProvider.GetUtcNow();
        List<(OutboxEntry Entry, OutboxMessage Message)>? candidates = null;
        List<Unavailable>? unavailable = null;
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
            if (GetExpirationTime(entry.State) <= now) (unavailable ??= []).Add(new(entry, UnavailableKind.Expired, message, error));
            else if (error is not null) (unavailable ??= []).Add(new(entry, UnavailableKind.Failed, null, error));
            else if (message is { } candidate) (candidates ??= []).Add((entry, candidate));
            else (unavailable ??= []).Add(new(entry, UnavailableKind.Unclaimed, null, null));
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
            return new(entry, message, result, null, Stopwatch.GetElapsedTime(start));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(entry, message, null, "Delivery was canceled.", Stopwatch.GetElapsedTime(start));
        }
        catch (Exception exception)
        {
            if (Logger.IsEnabled(LogLevel.Error))
            {
                LogDeliveryError(Logger, exception, message.Envelope.MessageId, message.ReceiverId);
            }
            return new(entry, message, null, exception.ToString(), Stopwatch.GetElapsedTime(start));
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

                var status = outcome.Result?.Status;
                if (status == DeliveryStatus.Expired)
                {
                    // Expired means this send is at or below the admission floor; earlier acceptance remains unknown.
                    var reason = outcome.Result!.Value.Message ?? "The message was sent before the receiver's deduplication window; its earlier delivery outcome is unknown.";
                    StageDeadLetter(
                        current,
                        current.Reference is null ? outcome.Message : CopySourceMessage(outcome.Message),
                        reason,
                        checked(current.State.AttemptCount + 1));
                }
                else if (status is DeliveryStatus.Accepted or DeliveryStatus.Duplicate or DeliveryStatus.DeadLettered)
                {
                    Emit(new() { Kind = OutboxCommandKind.Delivery, MessageId = id });
                    Retire(current, status == DeliveryStatus.DeadLettered
                        ? OutboxMessageRetirement.Rejected(outcome.Result!.Value.Message ?? "The receiver dead-lettered the message.", outcome.Result.Value.Code)
                        : OutboxMessageRetirement.Delivered());
                    delivered++;
                }
                else
                {
                    if (status == DeliveryStatus.Backpressured) backpressured++;
                    var attemptCount = checked(current.State.AttemptCount + 1);
                    var error = outcome.Error ?? status switch
                    {
                        DeliveryStatus.Backpressured => "The receiver is backpressured.",
                        DeliveryStatus.HandlerNotFound => outcome.Result?.Message ?? "The receiver has no registered inbox handler.",
                        _ => $"Unexpected delivery status '{status}'."
                    };
                    if (PrepareRetry(outcome, attemptCount, error) is { } retry)
                    {
                        Emit(new() { Kind = OutboxCommandKind.Delivery, MessageId = id, State = retry });
                        MessagingEvents.EmitOutboxRetryScheduled(
                            _pump.GrainContext, id, outcome.Message.ReceiverId, attemptCount, retry.NextAttemptAt!.Value, error);
                    }
                    else
                    {
                        // The source may release a reference entry's payload and headers when it retires, so the dead letter keeps a copy.
                        StageDeadLetter(current, current.Reference is null ? outcome.Message : CopySourceMessage(outcome.Message), error, attemptCount);
                    }
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
    private async ValueTask DeadLetterUnavailableAsync(List<Unavailable> unavailable)
    {
        try
        {
            foreach (var (entry, kind, message, error) in unavailable)
            {
                var id = entry.MessageId;
                switch (kind)
                {
                    case UnavailableKind.Expired:
                        LogMessageExpired(Logger, id);
                        StageDeadLetter(
                            entry,
                            message is not { } expired ? CreateUnmaterializedMessage(id) : entry.Reference is null ? expired : CopySourceMessage(expired),
                            entry.State.LastError is { } last
                                ? $"The outbox retry timeout elapsed after the last attempt failed: {last}"
                                : "The outbox retry timeout elapsed before the message was delivered.",
                            entry.State.AttemptCount);
                        Shared.Instruments.OnOutboxMessageDelivered(Shared.GrainType, "expired");
                        break;
                    case UnavailableKind.Failed:
                        LogSourceFailed(Logger, error!, id);
                        StageDeadLetter(entry, CreateUnmaterializedMessage(id),
                            $"The outbox message source failed to provide the message: {error}", checked(entry.State.AttemptCount + 1));
                        Shared.Instruments.OnOutboxMessageDelivered(Shared.GrainType, "source_error");
                        break;
                    default:
                        LogMessageUnprovided(Logger, id);
                        StageDeadLetter(entry, CreateUnmaterializedMessage(id),
                            "No registered outbox message source provides the message.", entry.State.AttemptCount);
                        Shared.Instruments.OnOutboxMessageDelivered(Shared.GrainType, "unprovided");
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
        Emit(new()
        {
            Kind = OutboxCommandKind.Delivery,
            MessageId = id,
            DeadLetter = new() { Message = message, DeadLetteredAt = now, Reason = reason, AttemptCount = attemptCount }
        });
        Retire(entry, retirement ?? OutboxMessageRetirement.DeadLettered(reason));
    }

    private bool CompactDeadLetters(DateTimeOffset now, int reservedCapacity = 0)
    {
        if (_deadLetters.Count == 0) return false;
        var retained = new Dictionary<MessageId, OutboxDeadLetter>(_deadLetters);
        if (!DeadLetterRetention.Compact(retained, now, Options.DeadLetterRetentionPeriod, Options.MaxRetainedDeadLetters,
            static entry => entry.DeadLetteredAt, reservedCapacity))
        {
            return false;
        }

        Emit(new()
        {
            Kind = OutboxCommandKind.RemoveDeadLetters,
            RemovedDeadLetters = _deadLetters.Keys.Where(key => !retained.ContainsKey(key)).ToArray()
        });
        return true;
    }
    private void RecordDeliveryMetrics(DeliveryOutcome outcome)
    {
        var message = outcome.Message;
        var status = outcome.Result?.Status;
        var metricStatus = status switch
        {
            DeliveryStatus.Accepted => "accepted",
            DeliveryStatus.Duplicate => "duplicate",
            DeliveryStatus.DeadLettered => "deadlettered",
            DeliveryStatus.Backpressured => "backpressured",
            DeliveryStatus.HandlerNotFound => "handler_not_found",
            DeliveryStatus.Expired => "expired",
            null => "error",
            _ => status.Value.ToString().ToLowerInvariant()
        };
        Shared.Instruments.OnOutboxMessageDelivered(Shared.GrainType, metricStatus);
        Shared.Instruments.OnOutboxDeliveryDuration(outcome.Duration, Shared.GrainType);
        switch (status)
        {
            case DeliveryStatus.Accepted:
            case DeliveryStatus.Duplicate:
            case DeliveryStatus.DeadLettered:
            case DeliveryStatus.Expired:
                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    LogMessageDelivered(Logger, message.Envelope.MessageId, message.ReceiverId, status.Value);
                }
                break;
            case DeliveryStatus.Backpressured:
                LogDeliveryBackpressured(Logger, message.Envelope.MessageId, message.ReceiverId);
                break;
            case DeliveryStatus.HandlerNotFound:
                if (Logger.IsEnabled(LogLevel.Error))
                {
                    LogDeliveryHandlerNotFound(Logger, message.Envelope.MessageId, message.ReceiverId, outcome.Result?.Message);
                }
                break;
            case { } unexpected:
                if (Logger.IsEnabled(LogLevel.Warning))
                {
                    LogUnexpectedDeliveryStatus(Logger, unexpected, message.Envelope.MessageId, message.ReceiverId);
                }
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

    private void ReportDepth(int delta) => Shared.Instruments.OnOutboxDepthChanged(Shared.GrainType, delta);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox command staging failed.")]
    private static partial void LogStagingFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No durable messages to deliver (all {Count} messages are still pending)")]
    private static partial void LogNoMessages(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Delivering {Count} durable messages from outbox")]
    private static partial void LogDeliveringMessages(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Delivered message {MessageId} to {ReceiverId} (Status: {Status})")]
    private static partial void LogMessageDelivered(ILogger logger, MessageId messageId, GrainId receiverId, DeliveryStatus status);

    [LoggerMessage(Level = LogLevel.Error, Message = "No inbox handler for message {MessageId} to {ReceiverId}: {Message}")]
    private static partial void LogDeliveryHandlerNotFound(ILogger logger, MessageId messageId, GrainId receiverId, string? message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Backpressured delivering message {MessageId} to {ReceiverId}, will retry later")]
    private static partial void LogDeliveryBackpressured(ILogger logger, MessageId messageId, GrainId receiverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unexpected delivery status {Status} for message {MessageId} to {ReceiverId}")]
    private static partial void LogUnexpectedDeliveryStatus(ILogger logger, DeliveryStatus status, MessageId messageId, GrainId receiverId);

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

    private readonly record struct DeliveryOutcome(
        OutboxEntry Entry,
        OutboxMessage Message,
        DeliveryResult? Result,
        string? Error,
        TimeSpan Duration);

    private enum UnavailableKind { Expired, Failed, Unclaimed }

    private readonly record struct Unavailable(OutboxEntry Entry, UnavailableKind Kind, OutboxMessage? Message, Exception? Error);
}

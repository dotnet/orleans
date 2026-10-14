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
using Orleans.Journaling;
using Orleans.Runtime;
using Polly;

namespace Orleans.Messaging;

/// <summary>
/// Delivers journal-acknowledged messages to their receivers' inboxes.
/// </summary>
internal sealed partial class Outbox : IStateMachine, IDurableValueCommandHandler<OutboxCommand>, IOutbox
{
    private readonly MessagingPump _pump;
    private JournalStreamWriter _writer;
    private readonly Dictionary<MessageId, OutboxEntry> _messages = [];
    private readonly Dictionary<MessageId, OutboxDeadLetter> _deadLetters = [];
    private readonly List<IOutboxMessageSource> _sources = [];
    private Task<DeliveryOutcome[]>? _deliveries;

    public Outbox(MessagingPump pump)
    {
        _pump = pump;
        pump.StateManager.RegisterStateMachine(MessagingStateNames.Outbox, this);
        pump.Register(this);
    }

    private MessagingShared Shared => _pump.Shared;
    private DurableMessagingOptions Options => _pump.Shared.Options;
    private ILogger Logger => _pump.Shared.OutboxLogger;
    // Delivery retries, expiry, and retention follow the durable jobs clock which schedules outbox wake-ups.
    private TimeProvider TimeProvider => _pump.Shared.JobTimeProvider;
    private GrainId GrainId => _pump.GrainContext.GrainId;

    internal IReadOnlyDictionary<MessageId, OutboxEntry> MessageState => _messages;
    internal IReadOnlyDictionary<MessageId, OutboxDeadLetter> DeadLetterState => _deadLetters;
    internal int Count => _messages.Count;

    /// <summary>Gets whether delivery attempts are in flight or awaiting application by the pump.</summary>
    internal bool IsDelivering => _deliveries is not null;

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
            _pump.Inbox.StageLocal(message.ToInboxMessage());
            Shared.Instruments.OnOutboxMessageSent(Shared.GrainType);
            return;
        }

        try
        {
            Emit(new() { Kind = OutboxCommandKind.Enqueue, Message = message, State = new() });
            Shared.Instruments.OnOutboxMessageSent(Shared.GrainType);
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
            Emit(new()
            {
                Kind = OutboxCommandKind.Enqueue,
                Reference = new() { MessageId = messageId },
                State = new()
            });
            _messages[messageId].Source = source;
            Shared.Instruments.OnOutboxMessageSent(Shared.GrainType);
        }
        catch (Exception exception)
        {
            FailStaging(exception);
        }
    }

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
    /// <see langword="false"/> when the source has withdrawn the message. Throws when the source throws or returns an
    /// invalid message.
    /// </summary>
    private bool TryGetMessage(OutboxEntry entry, out OutboxMessage message)
    {
        if (entry.Message is { } ordinary)
        {
            message = ordinary;
            return true;
        }

        if (entry.Source is { } owner) return TryGetSourceMessage(owner, entry.MessageId, out message);

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
    private static void Retire(OutboxEntry entry, OutboxMessageRetirement retirement)
    {
        if (entry.Reference is not null) entry.Source!.OnRetired(entry.MessageId, retirement);
    }

    /// <summary>
    /// Creates the message of a dead letter whose source failed to provide it: the message ID with an empty payload and
    /// a default receiver, which no valid outgoing message has.
    /// </summary>
    private static OutboxMessage CreateUnmaterializedMessage(MessageId messageId) =>
        new() { Envelope = new() { MessageId = messageId, Payload = [] }, ReceiverId = default };

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

    private List<(OutboxEntry Entry, OutboxMessage Message)>? SelectMessages(
        ref List<OutboxEntry>? withdrawn,
        ref List<(OutboxEntry Entry, Exception Error)>? failed)
    {
        var now = TimeProvider.GetUtcNow();
        List<(OutboxEntry Entry, OutboxMessage Message)>? candidates = null;
        foreach (var entry in _messages.Values)
        {
            if (entry.Sequence <= _durableSequence && IsReadyForAttempt(entry.State, now))
            {
                OutboxMessage message;
                try
                {
                    if (!TryGetMessage(entry, out message))
                    {
                        (withdrawn ??= []).Add(entry);
                        continue;
                    }
                }
                catch (Exception exception)
                {
                    // The entry is durable, so a source which can't provide a valid message dead-letters only that entry.
                    (failed ??= []).Add((entry, exception));
                    continue;
                }

                (candidates ??= []).Add((entry, message));
                if (candidates.Count == Options.OutboxBatchSize) break;
            }
        }
        if (candidates is null)
        {
            LogNoMessages(Logger, Count);
            return null;
        }
        LogDeliveringMessages(Logger, candidates.Count);
        return candidates;
    }

    private async Task<DeliveryOutcome> DeliverAsync(OutboxEntry entry, OutboxMessage message, CancellationToken cancellationToken)
    {
        var startedAt = TimeProvider.GetUtcNow();
        if (IsRetryExpired(entry.State, startedAt))
        {
            return new(entry, message, null, entry.State.LastError, TimeSpan.Zero, startedAt, true);
        }
        var start = Stopwatch.GetTimestamp();
        try
        {
            var target = Shared.GrainFactory.GetGrain<IMessagingExtension>(message.ReceiverId);
            var inboxMessage = message.ToInboxMessage();
            var pipeline = Options.DeliveryResiliencePipeline;
            DeliveryResult result;
            if (ReferenceEquals(pipeline, ResiliencePipeline.Empty))
            {
                result = await target.DeliverAsync(inboxMessage, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                // Continue on the activation's scheduler so each try calls the receiver from this grain.
                var context = ResilienceContextPool.Shared.Get(continueOnCapturedContext: true, cancellationToken);
                try
                {
                    result = await pipeline.ExecuteAsync(
                        static (context, state) => state.target.DeliverAsync(state.inboxMessage, context.CancellationToken),
                        context,
                        (target, inboxMessage)).ConfigureAwait(true);
                }
                finally
                {
                    ResilienceContextPool.Shared.Return(context);
                }
            }

            return new(entry, message, result, null, Stopwatch.GetElapsedTime(start), startedAt, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(entry, message, null, "Delivery was canceled.", Stopwatch.GetElapsedTime(start), startedAt, false);
        }
        catch (Exception exception)
        {
            if (Logger.IsEnabled(LogLevel.Error))
            {
                LogDeliveryError(Logger, exception, message.Envelope.MessageId, message.ReceiverId);
            }
            return new(entry, message, null, exception.ToString(), Stopwatch.GetElapsedTime(start), startedAt, false);
        }
    }

    /// <summary>Advances outbox delivery and returns whether delivery outcomes were applied.</summary>
    internal async ValueTask<bool> AdvanceAsync(CancellationToken cancellationToken)
    {
        if (_deliveries is { } pending)
        {
            if (!pending.IsCompleted) return false;
            _deliveries = null;
            cancellationToken.ThrowIfCancellationRequested();
            await ApplyDeliveriesAsync(pending.Result).ConfigureAwait(true);
            return true;
        }

        List<OutboxEntry>? withdrawn = null;
        List<(OutboxEntry Entry, Exception Error)>? failed = null;
        var candidates = SelectMessages(ref withdrawn, ref failed);
        var removed = withdrawn is not null || failed is not null;
        if (removed) await RemoveUnavailableAsync(withdrawn, failed).ConfigureAwait(true);
        if (candidates is null) return removed;
        var attempts = new Task<DeliveryOutcome>[candidates.Count];
        for (var index = 0; index < attempts.Length; index++)
        {
            var (entry, message) = candidates[index];
            attempts[index] = DeliverAsync(entry, message, _pump.ShutdownToken);
        }

        var deliveries = Task.WhenAll(attempts);
        if (!deliveries.IsCompleted)
        {
            _deliveries = deliveries;
            SignalOnCompletionAsync(deliveries).Ignore();
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await ApplyDeliveriesAsync(deliveries.Result).ConfigureAwait(true);
        return true;
    }

    private async Task SignalOnCompletionAsync(Task deliveries)
    {
        await deliveries.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        _pump.Signal();
    }

    private async ValueTask ApplyDeliveriesAsync(DeliveryOutcome[] outcomes)
    {
        Dictionary<MessageId, OutboxDeadLetter>? deadLetters = null;
        int applied = 0, delivered = 0, backpressured = 0;
        try
        {
            foreach (var outcome in outcomes)
            {
                // Outcomes for entries which were replaced or removed since the attempt started are stale.
                var id = outcome.Entry.MessageId;
                if (!_messages.TryGetValue(id, out var current) || !ReferenceEquals(current, outcome.Entry)) continue;

                var status = outcome.Result?.Status;
                if (status is DeliveryStatus.Accepted or DeliveryStatus.Duplicate or DeliveryStatus.DeadLettered)
                {
                    Emit(new() { Kind = OutboxCommandKind.Delivery, MessageId = id });
                    Retire(current, status == DeliveryStatus.DeadLettered ? OutboxMessageRetirement.DeadLettered : OutboxMessageRetirement.Delivered);
                    delivered++;
                }
                else
                {
                    if (status == DeliveryStatus.Backpressured) backpressured++;
                    var (retry, deadLetter) = PrepareRetry(outcome, ref deadLetters);
                    Emit(new() { Kind = OutboxCommandKind.Delivery, MessageId = id, State = retry, DeadLetter = deadLetter });
                    if (deadLetter is not null) Retire(current, OutboxMessageRetirement.DeadLettered);
                }
                RecordDeliveryMetrics(outcome);
                applied++;
            }
            if (deadLetters is not null) ApplyDeadLetters(deadLetters);
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
    /// Removes reference entries whose sources withdrew their messages, without delivering or retiring them, and
    /// dead-letters entries whose sources failed to provide a valid message.
    /// </summary>
    private async ValueTask RemoveUnavailableAsync(List<OutboxEntry>? withdrawn, List<(OutboxEntry Entry, Exception Error)>? failed)
    {
        try
        {
            if (withdrawn is not null)
            {
                foreach (var entry in withdrawn)
                {
                    Emit(new() { Kind = OutboxCommandKind.Delivery, MessageId = entry.MessageId });
                    Shared.Instruments.OnOutboxMessageDelivered(Shared.GrainType, "withdrawn");
                    if (entry.Source is null)
                    {
                        LogMessageUnprovided(Logger, entry.MessageId);
                    }
                    else
                    {
                        LogMessageWithdrawn(Logger, entry.MessageId);
                    }
                }
            }

            if (failed is not null) DeadLetterSourceFailures(failed);
        }
        catch (Exception exception)
        {
            FailStaging(exception);
        }

        await PersistAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Dead-letters reference entries whose owning sources threw or returned an invalid message, and retires them with
    /// their sources in the same journal write.
    /// </summary>
    /// <remarks>
    /// No valid message exists, so each dead letter carries only the message ID; see <see cref="CreateUnmaterializedMessage"/>.
    /// The failed materialization counts as a delivery attempt.
    /// </remarks>
    private void DeadLetterSourceFailures(List<(OutboxEntry Entry, Exception Error)> failed)
    {
        var now = TimeProvider.GetUtcNow();
        Dictionary<MessageId, OutboxDeadLetter>? deadLetters = null;
        foreach (var (entry, error) in failed)
        {
            var id = entry.MessageId;
            LogSourceFailed(Logger, error, id);
            var letter = new OutboxDeadLetter
            {
                Message = CreateUnmaterializedMessage(id),
                DeadLetteredAt = now,
                Reason = $"The outbox message source failed to provide the message: {error}",
                AttemptCount = checked(entry.State.AttemptCount + 1)
            };
            AddDeadLetter(ref deadLetters, letter);
            Emit(new() { Kind = OutboxCommandKind.Delivery, MessageId = id, DeadLetter = letter });
            Retire(entry, OutboxMessageRetirement.DeadLettered);
            Shared.Instruments.OnOutboxMessageDelivered(Shared.GrainType, "source_error");
        }

        ApplyDeadLetters(deadLetters!);
    }

    /// <summary>Adds a dead letter to the retained dead letters, compacting them to make room.</summary>
    private void AddDeadLetter(ref Dictionary<MessageId, OutboxDeadLetter>? deadLetters, OutboxDeadLetter letter)
    {
        var key = letter.Message.Envelope.MessageId;
        deadLetters ??= _deadLetters.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        DeadLetterRetention.Compact(deadLetters, letter.DeadLetteredAt, Options.DeadLetterRetentionPeriod,
            Options.MaxRetainedDeadLetters, static entry => entry.DeadLetteredAt,
            reservedCapacity: deadLetters.ContainsKey(key) ? 0 : 1);
        deadLetters[key] = letter;
    }

    private void ApplyDeadLetters(Dictionary<MessageId, OutboxDeadLetter> entries)
    {
        foreach (var key in _deadLetters.Keys.Where(key => !entries.ContainsKey(key)).ToArray())
        {
            Emit(new() { Kind = OutboxCommandKind.RemoveDeadLetters, RemovedDeadLetters = [key] });
        }
        foreach (var entry in entries)
        {
            if (!_deadLetters.TryGetValue(entry.Key, out var existing) || !ReferenceEquals(existing, entry.Value))
            {
                Emit(new() { Kind = OutboxCommandKind.Delivery, MessageId = entry.Key, DeadLetter = entry.Value });
            }
        }
    }

    private (OutboxMessageState? Retry, OutboxDeadLetter? DeadLetter) PrepareRetry(
        DeliveryOutcome outcome,
        ref Dictionary<MessageId, OutboxDeadLetter>? deadLetters)
    {
        var previous = outcome.Entry.State;
        var status = outcome.Result?.Status;
        var now = TimeProvider.GetUtcNow();
        var attemptCount = outcome.Expired ? previous.AttemptCount : checked(previous.AttemptCount + 1);

        // A receiver checks its deduplication records before it answers, so a Backpressured answer proves that no earlier
        // attempt was accepted. Only attempts since then can have been accepted, so retries expire OutboxRetryTimeout after
        // the first of them, within the receiver's deduplication window.
        var expirationTime = status is DeliveryStatus.Backpressured
            ? (DateTimeOffset?)null
            : previous.ExpirationTime ?? MessagingTime.AddClamped(outcome.StartedAt, Options.OutboxRetryTimeout);
        var error = outcome.Error ?? status switch
        {
            DeliveryStatus.Backpressured => "The receiver is backpressured.",
            DeliveryStatus.HandlerNotFound => outcome.Result?.Message ?? "The receiver has no registered inbox handler.",
            _ => $"Unexpected delivery status '{status}'."
        };
        if (outcome.Expired
            || status is not (null or DeliveryStatus.Backpressured or DeliveryStatus.HandlerNotFound)
            || expirationTime <= now)
        {
            var letter = new OutboxDeadLetter
            {
                Message = outcome.Message,
                DeadLetteredAt = now,
                Reason = error,
                AttemptCount = attemptCount
            };
            AddDeadLetter(ref deadLetters, letter);
            return (null, letter);
        }

        // Failed attempts return to the outbox with a durable backoff, so later batches deliver other messages meanwhile.
        var exponent = Math.Min(attemptCount - 1, DurableMessagingOptions.MaximumBackoffExponent);
        return (new OutboxMessageState
        {
            AttemptCount = attemptCount,
            LastError = error,
            ExpirationTime = expirationTime,
            NextAttemptAt = MessagingTime.AddClamped(now, TimeSpan.FromTicks(Options.RetryInitialInterval.Ticks * (1L << exponent)))
        }, null);
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

    private static bool IsRetryExpired(OutboxMessageState state, DateTimeOffset now) => state.ExpirationTime <= now;

    private bool IsReadyForAttempt(OutboxMessageState state, DateTimeOffset now) =>
        IsRetryExpired(state, now) || state.NextAttemptAt is null || state.NextAttemptAt <= now;

    private DateTimeOffset GetNextAttemptAt(OutboxMessageState state, DateTimeOffset now)
    {
        var retryAt = state.NextAttemptAt ?? now;
        return state.ExpirationTime < retryAt ? state.ExpirationTime.Value : retryAt;
    }

    /// <summary>
    /// Returns the delay until the next delivery attempt is due, or <see langword="null"/> when the outbox is empty.
    /// </summary>
    /// <param name="now">The current Durable Jobs time.</param>
    internal TimeSpan? GetNextWakeupDelay(DateTimeOffset now)
    {
        if (Count == 0) return null;
        var next = DateTimeOffset.MaxValue;
        foreach (var entry in _messages.Values)
        {
            var at = GetNextAttemptAt(entry.State, now);
            if (at < next) next = at;
        }
        return next > now ? next - now : TimeSpan.Zero;
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_deadLetters.Count > Options.MaxRetainedDeadLetters || _deadLetters.Values.Any(entry =>
            MessagingTime.IsExpired(TimeProvider.GetUtcNow(), entry.DeadLetteredAt, Options.DeadLetterRetentionPeriod)))
        {
            var retained = _deadLetters.ToDictionary(static pair => pair.Key, static pair => pair.Value);
            DeadLetterRetention.Compact(retained, TimeProvider.GetUtcNow(),
                Options.DeadLetterRetentionPeriod, Options.MaxRetainedDeadLetters, static entry => entry.DeadLetteredAt);
            try
            {
                ApplyDeadLetters(retained);
            }
            catch (Exception exception)
            {
                FailStaging(exception);
            }
            await PersistAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Waits for in-flight deliveries, which observe the pump's shutdown token.</summary>
    internal Task StopAsync() => (Task?)_deliveries ?? Task.CompletedTask;

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removed outbox message {MessageId}, which no registered outbox message source provides")]
    private static partial void LogMessageUnprovided(ILogger logger, MessageId messageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The outbox message source failed to provide message {MessageId}; the message is dead-lettered")]
    private static partial void LogSourceFailed(ILogger logger, Exception exception, MessageId messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox delivery complete: {DeliveredCount} delivered, {BackpressuredCount} backpressured, {FailedCount} failed, {RemainingCount} remaining")]
    private static partial void LogDeliveryComplete(ILogger logger, int deliveredCount, int backpressuredCount, int failedCount, int remainingCount);

    private readonly record struct DeliveryOutcome(
        OutboxEntry Entry,
        OutboxMessage Message,
        DeliveryResult? Result,
        string? Error,
        TimeSpan Duration,
        DateTimeOffset StartedAt,
        bool Expired);
}

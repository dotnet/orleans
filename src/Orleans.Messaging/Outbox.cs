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
            Emit(new() { Kind = OutboxCommandKind.Enqueue, Message = message, State = new() { EnqueuedAt = TimeProvider.GetUtcNow() } });
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
                State = new() { EnqueuedAt = TimeProvider.GetUtcNow() }
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
    /// <see langword="false"/> when the source has withdrawn the message.
    /// </summary>
    private bool TryGetMessage(OutboxEntry entry, out OutboxMessage message)
    {
        if (entry.Message is { } ordinary)
        {
            message = ordinary;
            return true;
        }

        if (entry.Source is { } owner) return TryGetSourceMessage(owner, entry.MessageId, out message);
        entry.Source = FindSource(entry.MessageId, out message);
        return entry.Source is not null;
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

            if (entry.State.EnqueuedAt is null)
            {
                throw new InvalidOperationException("A recovered outbox message requires its enqueue time.");
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
            EnvelopeValidation.Validate(letter.Message);
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

    private List<(OutboxEntry Entry, OutboxMessage Message)>? SelectMessages(ref List<OutboxEntry>? withdrawn)
    {
        var now = TimeProvider.GetUtcNow();
        List<(OutboxEntry Entry, OutboxMessage Message)>? candidates = null;
        foreach (var entry in _messages.Values)
        {
            if (entry.Sequence <= _durableSequence && IsReadyForAttempt(entry.State, now))
            {
                // An invalid source message fails the pass before any attempt starts.
                if (!TryGetMessage(entry, out var message))
                {
                    (withdrawn ??= []).Add(entry);
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
        if (entry.State.EnqueuedAt is { } enqueuedAt
            && MessagingTime.IsExpired(TimeProvider.GetUtcNow(), enqueuedAt, Options.MaxOutboxRetryAge))
        {
            return new(entry, message, null, $"The message exceeded the maximum retry age of {Options.MaxOutboxRetryAge}.", TimeSpan.Zero, true);
        }
        var start = Stopwatch.GetTimestamp();
        try
        {
            var target = Shared.GrainFactory.GetGrain<IInboxExtension>(message.ReceiverId);
            var result = await target.DeliverAsync(message.ToInboxMessage(), cancellationToken).ConfigureAwait(true);
            return new(entry, message, result, null, Stopwatch.GetElapsedTime(start), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(entry, message, null, "Delivery was canceled.", Stopwatch.GetElapsedTime(start), false);
        }
        catch (Exception exception)
        {
            if (Logger.IsEnabled(LogLevel.Error))
            {
                LogDeliveryError(Logger, exception, message.Envelope.MessageId, message.ReceiverId);
            }
            return new(entry, message, null, exception.ToString(), Stopwatch.GetElapsedTime(start), false);
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
        var candidates = SelectMessages(ref withdrawn);
        if (withdrawn is not null) await RemoveWithdrawnAsync(withdrawn).ConfigureAwait(true);
        if (candidates is null) return withdrawn is not null;
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

    /// <summary>Removes reference entries whose sources withdrew their messages, without delivering or retiring them.</summary>
    private async ValueTask RemoveWithdrawnAsync(List<OutboxEntry> withdrawn)
    {
        try
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
        catch (Exception exception)
        {
            FailStaging(exception);
        }

        await PersistAsync().ConfigureAwait(true);
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
        var error = outcome.Error ?? status switch
        {
            DeliveryStatus.Backpressured => "The receiver is backpressured.",
            DeliveryStatus.HandlerNotFound => outcome.Result?.Message ?? "The receiver has no registered inbox handler.",
            _ => $"Unexpected delivery status '{status}'."
        };
        if (outcome.Expired || attemptCount >= Options.MaxDeliveryAttempts
            || MessagingTime.IsExpired(now, previous.EnqueuedAt!.Value, Options.MaxOutboxRetryAge))
        {
            var letter = new OutboxDeadLetter
            {
                Message = outcome.Message,
                DeadLetteredAt = now,
                Reason = error,
                AttemptCount = attemptCount
            };
            deadLetters ??= _deadLetters.ToDictionary(static pair => pair.Key, static pair => pair.Value);
            DeadLetterRetention.Compact(deadLetters, now, Options.DeadLetterRetentionPeriod,
                Options.MaxRetainedDeadLetters, static entry => entry.DeadLetteredAt,
                reservedCapacity: deadLetters.ContainsKey(letter.Message.Envelope.MessageId) ? 0 : 1);
            deadLetters[letter.Message.Envelope.MessageId] = letter;
            return (null, letter);
        }
        var exponent = Math.Min(attemptCount - 1, DurableMessagingOptions.MaximumBackoffExponent);
        return (new OutboxMessageState
        {
            AttemptCount = attemptCount,
            LastError = error,
            EnqueuedAt = previous.EnqueuedAt,
            NextAttemptAt = MessagingTime.AddClamped(now, TimeSpan.FromTicks(Options.BackpressureRetryDelay.Ticks * (1L << exponent)))
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
                if (Logger.IsEnabled(LogLevel.Warning))
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

    private bool IsReadyForAttempt(OutboxMessageState state, DateTimeOffset now) =>
        state.EnqueuedAt is { } enqueuedAt && MessagingTime.IsExpired(now, enqueuedAt, Options.MaxOutboxRetryAge)
        || state.NextAttemptAt is null || state.NextAttemptAt <= now;

    private DateTimeOffset GetNextAttemptAt(OutboxMessageState state, DateTimeOffset now)
    {
        var retryAt = state.NextAttemptAt ?? now;
        var expiresAt = state.EnqueuedAt is { } enqueuedAt ? MessagingTime.AddClamped(enqueuedAt, Options.MaxOutboxRetryAge) : retryAt;
        return retryAt <= expiresAt ? retryAt : expiresAt;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "No inbox handler for message {MessageId} to {ReceiverId}: {Message}")]
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox delivery complete: {DeliveredCount} delivered, {BackpressuredCount} backpressured, {FailedCount} failed, {RemainingCount} remaining")]
    private static partial void LogDeliveryComplete(ILogger logger, int deliveredCount, int backpressuredCount, int failedCount, int remainingCount);

    private readonly record struct DeliveryOutcome(OutboxEntry Entry, OutboxMessage Message, DeliveryResult? Result, string? Error, TimeSpan Duration, bool Expired);
}

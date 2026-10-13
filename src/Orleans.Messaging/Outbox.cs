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
    private readonly Dictionary<HierarchicalKey, OutboxEntry> _messages = [];
    private readonly Dictionary<HierarchicalKey, OutboxDeadLetter> _deadLetters = [];
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

    internal IReadOnlyDictionary<HierarchicalKey, OutboxEntry> MessageState => _messages;
    internal IReadOnlyDictionary<HierarchicalKey, OutboxDeadLetter> DeadLetterState => _deadLetters;
    internal int Count => _messages.Count;

    public void Send(OutboxMessage message)
    {
        _pump.ShutdownToken.ThrowIfCancellationRequested();
        EnvelopeValidation.Validate(message);
        if (_messages.TryGetValue(message.Envelope.MessageId, out var existing))
        {
            if (!EnvelopeEquivalence.AreEquivalent(existing.Message, message))
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

    private void OnRecoveryCompleted()
    {
        foreach (var (key, entry) in _messages)
        {
            var message = entry.Message;
            EnvelopeValidation.Validate(message);
            if (entry.State.EnqueuedAt is null)
            {
                throw new InvalidOperationException("A recovered outbox message requires its enqueue time.");
            }
            if (key != message.Envelope.MessageId)
            {
                throw new InvalidOperationException("The recovered outbox key does not match its command identity.");
            }
            if (message.ReceiverId == GrainId)
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

    private List<OutboxEntry>? SelectMessages()
    {
        var now = TimeProvider.GetUtcNow();
        List<OutboxEntry>? candidates = null;
        foreach (var entry in _messages.Values)
        {
            if (entry.Sequence <= _durableSequence && IsReadyForAttempt(entry.State, now))
            {
                (candidates ??= []).Add(entry);
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

    private async Task<DeliveryOutcome> DeliverAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        if (entry.State.EnqueuedAt is { } enqueuedAt
            && MessagingTime.IsExpired(TimeProvider.GetUtcNow(), enqueuedAt, Options.MaxOutboxRetryAge))
        {
            return new(entry, null, $"The message exceeded the maximum retry age of {Options.MaxOutboxRetryAge}.", TimeSpan.Zero, true);
        }
        var start = Stopwatch.GetTimestamp();
        var message = entry.Message;
        try
        {
            var target = Shared.GrainFactory.GetGrain<IInboxExtension>(message.ReceiverId);
            var result = await target.DeliverAsync(message.ToInboxMessage(), cancellationToken).ConfigureAwait(true);
            return new(entry, result, null, Stopwatch.GetElapsedTime(start), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(entry, null, "Delivery was canceled.", Stopwatch.GetElapsedTime(start), false);
        }
        catch (Exception exception)
        {
            if (Logger.IsEnabled(LogLevel.Error))
            {
                LogDeliveryError(Logger, exception, message.Envelope.MessageId, message.ReceiverId);
            }
            return new(entry, null, exception.ToString(), Stopwatch.GetElapsedTime(start), false);
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

        var candidates = SelectMessages();
        if (candidates is null) return false;
        var attempts = new Task<DeliveryOutcome>[candidates.Count];
        for (var index = 0; index < attempts.Length; index++)
        {
            attempts[index] = DeliverAsync(candidates[index], _pump.ShutdownToken);
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
        Dictionary<HierarchicalKey, OutboxDeadLetter>? deadLetters = null;
        int applied = 0, delivered = 0, backpressured = 0;
        try
        {
            foreach (var outcome in outcomes)
            {
                // Outcomes for entries which were replaced or removed since the attempt started are stale.
                var id = outcome.Entry.Message.Envelope.MessageId;
                if (!_messages.TryGetValue(id, out var current) || !ReferenceEquals(current, outcome.Entry)) continue;

                var status = outcome.Result?.Status;
                if (status is DeliveryStatus.Accepted or DeliveryStatus.Duplicate or DeliveryStatus.DeadLettered)
                {
                    Emit(new() { Kind = OutboxCommandKind.Delivery, MessageId = id });
                    delivered++;
                }
                else
                {
                    if (status == DeliveryStatus.Backpressured) backpressured++;
                    var (retry, deadLetter) = PrepareRetry(outcome, ref deadLetters);
                    Emit(new() { Kind = OutboxCommandKind.Delivery, MessageId = id, State = retry, DeadLetter = deadLetter });
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

    private void ApplyDeadLetters(Dictionary<HierarchicalKey, OutboxDeadLetter> entries)
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
        ref Dictionary<HierarchicalKey, OutboxDeadLetter>? deadLetters)
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
                Message = outcome.Entry.Message,
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
        var message = outcome.Entry.Message;
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
    private static partial void LogMessageDelivered(ILogger logger, HierarchicalKey messageId, GrainId receiverId, DeliveryStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No inbox handler for message {MessageId} to {ReceiverId}: {Message}")]
    private static partial void LogDeliveryHandlerNotFound(ILogger logger, HierarchicalKey messageId, GrainId receiverId, string? message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Backpressured delivering message {MessageId} to {ReceiverId}, will retry later")]
    private static partial void LogDeliveryBackpressured(ILogger logger, HierarchicalKey messageId, GrainId receiverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unexpected delivery status {Status} for message {MessageId} to {ReceiverId}")]
    private static partial void LogUnexpectedDeliveryStatus(ILogger logger, DeliveryStatus status, HierarchicalKey messageId, GrainId receiverId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error delivering message {MessageId} to {ReceiverId}")]
    private static partial void LogDeliveryError(ILogger logger, Exception exception, HierarchicalKey messageId, GrainId receiverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox delivery complete: {DeliveredCount} delivered, {BackpressuredCount} backpressured, {FailedCount} failed, {RemainingCount} remaining")]
    private static partial void LogDeliveryComplete(ILogger logger, int deliveredCount, int backpressuredCount, int failedCount, int remainingCount);

    private readonly record struct DeliveryOutcome(OutboxEntry Entry, DeliveryResult? Result, string? Error, TimeSpan Duration, bool Expired);
}

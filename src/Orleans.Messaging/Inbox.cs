using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Journaling;
using Orleans.Messaging.Configuration;
using Orleans.Runtime;

namespace Orleans.Messaging;

/// <summary>
/// Accepts, deduplicates, and processes durable incoming messages for one grain.
/// </summary>
/// <remarks>
/// All state transitions run on the activation scheduler, so each check-then-emit sequence is atomic.
/// Accepted messages are processed only after their acceptance is durable and their <see cref="Envelope.DeliverAt"/>,
/// if any, has passed.
/// </remarks>
internal sealed partial class Inbox : IInbox, IMessagingExtension, IStateMachine, IDurableValueCommandHandler<InboxCommand>
{
    private readonly MessagingPump _pump;
    private readonly InboxDispatcher _handlers = new([]);
    private readonly List<IInboxMessageSink> _sinks = [];
    private readonly List<IInboxWorkSource> _workSources = [];

    public Inbox(MessagingPump pump)
    {
        _pump = pump;
        pump.StateManager.RegisterStateMachine(MessagingStateNames.Inbox, this);
        pump.Register(this);
    }

    private MessagingShared Shared => _pump.Shared;
    private DurableMessagingOptions Options => _pump.Shared.Options;
    private ILogger Logger => _pump.Shared.InboxLogger;
    private TimeProvider TimeProvider => _pump.Shared.TimeProvider;
    private IGrainContext GrainContext => _pump.GrainContext;

    internal int Count => _messages.Count;

    internal IReadOnlyDictionary<MessageId, InboxEntry> MessageState => _messages;

    internal IReadOnlyDictionary<MessageId, InboxDeadLetter> DeadLetterState => _deadLetters;

    internal InboxDispatcher Handlers => _handlers;

    public IInbox Register(IInboxHandler handler)
    {
        _handlers.Add(handler);
        return this;
    }

    public IInbox Register(IInboxMessageSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sinks.Add(sink);
        return this;
    }

    public IInbox Register(IInboxWorkSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _workSources.Add(source);
        return this;
    }

    public async ValueTask<DeliveryResult> DeliverAsync(InboxMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _pump.ShutdownToken.ThrowIfCancellationRequested();
        EnvelopeValidation.Validate(message);

        // Delivery is a grain call, so the activation is servicing requests.
        _pump.EnsureStarted();
        var key = message.Envelope.MessageId;
        try
        {
            if (IsDuplicate(message, out var pending))
            {
                // The sender may retire its copy once this returns, so the acceptance must be durable. A sink acceptance
                // journals only its processed record, which can still be in flight.
                if (pending is null ? _sinks.Count > 0 : !IsDurable(pending)) await PersistForCallerAsync(cancellationToken).ConfigureAwait(true);
                if (pending is not null) _pump.Signal();

                Shared.Instruments.OnInboxMessageReceived(Shared.GrainType, "duplicate");
                return DeliveryResult.Duplicate();
            }

            // Sink-owned messages never occupy inbox capacity and need no handler. A sink's duplicate can still be staged,
            // so every outcome except backpressure is reported once the write is durable.
            if (StageSinkAcceptance(message) is { } sinkResult)
            {
                if (sinkResult.Status != DeliveryStatus.Backpressured) await PersistForCallerAsync(cancellationToken).ConfigureAwait(true);
                return sinkResult;
            }

            // Scheduled messages never count toward capacity, so they are not backpressured either.
            if (message.Envelope.DeliverAt is null && _messages.Count - _scheduledCount >= Options.MaxCapacity)
            {
                Shared.Instruments.OnInboxMessageReceived(Shared.GrainType, "backpressured");
                return DeliveryResult.Backpressured();
            }

            if (_handlers.IsEmpty)
            {
                Shared.Instruments.OnInboxMessageReceived(Shared.GrainType, "handler_not_found");
                LogHandlerNotFound(Logger, key, GrainContext.GrainId);
                return DeliveryResult.HandlerNotFound();
            }

            Emit(new() { Kind = InboxCommandKind.Accept, Message = message });
            await PersistForCallerAsync(cancellationToken).ConfigureAwait(true);
            Shared.Instruments.OnInboxMessageReceived(Shared.GrainType, "accepted");
            LogMessageAccepted(Logger, key, GrainContext.GrainId);
            return DeliveryResult.Accepted();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogDeliveryOperationFailed(Logger, exception, key, GrainContext.GrainId);
            throw;
        }
    }

    /// <summary>
    /// Offers a message to the registered sinks and stages the outcome which the first sink to take the message reports.
    /// </summary>
    /// <returns>The delivery result, or <see langword="null"/> when every sink declines the message.</returns>
    /// <remarks>
    /// An accepted message journals only its processed record, in the write which commits the sink's staged changes.
    /// A duplicate or backpressured message stages nothing. A sink which dead-letters the message, throws, or reports
    /// another status stages nothing itself, so the message is dead-lettered with its payload and recorded as processed.
    /// </remarks>
    private DeliveryResult? StageSinkAcceptance(InboxMessage message)
    {
        var key = message.Envelope.MessageId;
        foreach (var sink in _sinks)
        {
            DeliveryResult result;
            Exception? error = null;
            try
            {
                if (!sink.TryAccept(message, out result)) continue;
                if (result.Status is not (DeliveryStatus.Accepted or DeliveryStatus.Duplicate or DeliveryStatus.Backpressured or DeliveryStatus.DeadLettered))
                {
                    throw new InvalidOperationException($"The inbox message sink reported the unsupported delivery status '{result.Status}'.");
                }
            }
            catch (Exception exception)
            {
                error = exception;
                result = DeliveryResult.DeadLettered(exception.Message);
            }

            if (result.Status is DeliveryStatus.Duplicate or DeliveryStatus.Backpressured)
            {
                Shared.Instruments.OnInboxMessageReceived(Shared.GrainType, result.Status == DeliveryStatus.Duplicate ? "duplicate" : "backpressured");
                return result;
            }

            var accepted = result.Status == DeliveryStatus.Accepted;
            var reason = result.Message ?? "The inbox message sink dead-lettered the message.";
            try
            {
                if (accepted)
                {
                    StageCompletion(key, deadLetter: null);
                }
                else
                {
                    LogSinkDeadLettered(Logger, error, key, GrainContext.GrainId, reason);
                    StageDeadLetter(message, reason, attemptCount: 1);
                }
            }
            catch (Exception exception)
            {
                // The sink's staged changes cannot commit without the processed record.
                LogStagingFailed(Logger, exception);
                _pump.Fail(exception, "Inbox staging failed.");
                throw;
            }

            Shared.Instruments.OnInboxMessageReceived(Shared.GrainType, "accepted");
            Shared.Instruments.OnInboxMessageProcessed(Shared.GrainType, accepted ? "success" : "dead_lettered");
            if (!accepted) return DeliveryResult.DeadLettered(reason);
            LogMessageAccepted(Logger, key, GrainContext.GrainId);
            return DeliveryResult.Accepted();
        }

        return null;
    }

    /// <summary>
    /// Returns whether the message was completed within the deduplication window or is pending, and rejects a pending
    /// command which differs from it.
    /// </summary>
    /// <param name="message">The received message.</param>
    /// <param name="pending">The pending entry, or <see langword="null"/> when the message was completed or is new.</param>
    private bool IsDuplicate(InboxMessage message, out InboxEntry? pending)
    {
        var key = message.Envelope.MessageId;
        if (_processed.TryGetValue(key, out var processedAt)
            && !MessagingTime.IsExpired(TimeProvider.GetUtcNow(), processedAt, Options.DeduplicationWindow))
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

        var now = TimeProvider.GetUtcNow();
        var jobNow = Shared.JobTimeProvider.GetUtcNow();
        var batch = new List<InboxEntry>(Math.Min(Options.InboxBatchSize, _messages.Count));
        foreach (var entry in _messages.Values)
        {
            if (IsDurable(entry) && GetDelay(entry, now, jobNow) == TimeSpan.Zero)
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
            await _handlers.HandleAsync(new InboxHandlerContext(entry.Message, execution), cancellationToken).ConfigureAwait(true);
            execution.ThrowIfRejected();
            if (!execution.Completed)
            {
                execution.Reject(new InvalidOperationException(
                    "Inbox handlers must call Complete or Fail before returning successfully."));
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

            // An exception leaves the outcome unknown, so the message retries until InboxRetryTimeout after its first
            // failure. The last attempt runs when the timeout elapses; Fail(reason) dead-letters a message at once.
            var attempts = checked(entry.State.AttemptCount + 1);
            var now = TimeProvider.GetUtcNow();
            var expirationTime = entry.State.ExpirationTime ?? MessagingTime.AddClamped(now, Options.InboxRetryTimeout);
            if (expirationTime <= now)
            {
                StageDeadLetter(entry.Message, error.Message, attempts);
                status = "dead_lettered";
            }
            else
            {
                var retryAt = MessagingTime.AddClamped(now,
                    TimeSpan.FromTicks(Options.RetryInitialInterval.Ticks * (1L << Math.Min(attempts - 1, DurableMessagingOptions.MaximumBackoffExponent))));
                Emit(new()
                {
                    Kind = InboxCommandKind.Retry,
                    MessageId = entry.Message.Envelope.MessageId,
                    State = new()
                    {
                        AttemptCount = attempts,
                        LastError = error.ToString(),
                        NextAttemptAt = retryAt < expirationTime ? retryAt : expirationTime,
                        ExpirationTime = expirationTime
                    }
                });
                status = "retry";
            }
        }
        else
        {
            status = execution.DeadLetterReason is null ? "success" : "dead_lettered";
        }

        Shared.Instruments.OnInboxMessageProcessed(Shared.GrainType, status);
        Shared.Instruments.OnInboxProcessingDuration(Stopwatch.GetElapsedTime(start), Shared.GrainType);
    }

    private bool IsCurrent(InboxEntry entry) =>
        _messages.TryGetValue(entry.Message.Envelope.MessageId, out var current) && ReferenceEquals(current, entry);

    private void StageCompletion(MessageId messageId, InboxDeadLetter? deadLetter) =>
        Emit(new()
        {
            Kind = InboxCommandKind.Complete,
            MessageId = messageId,
            ProcessedAt = TimeProvider.GetUtcNow(),
            DeadLetter = deadLetter
        });

    private void StageDeadLetter(InboxMessage message, string reason, int attemptCount)
    {
        var now = TimeProvider.GetUtcNow();
        var key = message.Envelope.MessageId;
        CompactDeadLetters(now, reservedCapacity: _deadLetters.ContainsKey(key) ? 0 : 1);
        StageCompletion(key, new InboxDeadLetter
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
        var retained = new Dictionary<MessageId, InboxDeadLetter>(_deadLetters);
        if (!DeadLetterRetention.Compact(retained, now, Options.DeadLetterRetentionPeriod, Options.MaxRetainedDeadLetters,
            static entry => entry.DeadLetteredAt, reservedCapacity))
        {
            return false;
        }

        Emit(new()
        {
            Kind = InboxCommandKind.RemoveDeadLetters,
            RemovedKeys = _deadLetters.Keys.Where(key => !retained.ContainsKey(key)).ToArray()
        });
        return true;
    }

    internal bool RemoveDeadLetter(MessageId messageId)
    {
        _pump.ShutdownToken.ThrowIfCancellationRequested();
        if (!_deadLetters.ContainsKey(messageId)) return false;
        Emit(new() { Kind = InboxCommandKind.RemoveDeadLetters, RemovedKeys = [messageId] });
        return true;
    }

    /// <summary>Gets a value indicating whether the inbox or a registered work source holds pending work.</summary>
    internal bool HasWork => _messages.Count > 0 || GetWorkSourceDelay() is not null;

    /// <summary>
    /// Returns the delay until the next message or work source is due, or <see langword="null"/> when neither the inbox
    /// nor any work source holds work.
    /// </summary>
    /// <param name="jobNow">The current Durable Jobs time, which the caller uses to schedule the wake-up.</param>
    internal TimeSpan? GetNextWakeupDelay(DateTimeOffset jobNow)
    {
        var next = GetWorkSourceDelay();
        if (_messages.Count == 0 || next <= TimeSpan.Zero) return next;
        var now = TimeProvider.GetUtcNow();
        foreach (var entry in _messages.Values)
        {
            var delay = GetDelay(entry, now, jobNow);
            if (delay == TimeSpan.Zero) return delay;
            if (next is null || delay < next) next = delay;
        }

        return next;
    }

    private TimeSpan? GetWorkSourceDelay()
    {
        TimeSpan? next = null;
        foreach (var source in _workSources)
        {
            if (source.GetWakeupDelay() is { } delay && (next is null || delay < next)) next = delay;
        }

        return next;
    }

    // DeliverAt is in the Durable Jobs time domain, which schedules wake-ups; retry backoff follows the grain clock.
    private static TimeSpan GetDelay(InboxEntry entry, DateTimeOffset now, DateTimeOffset jobNow)
    {
        var delay = TimeSpan.Zero;
        if (entry.Message.Envelope.DeliverAt is { } deliverAt && deliverAt > jobNow) delay = deliverAt - jobNow;
        if (entry.State.NextAttemptAt is { } retryAt && retryAt > now && retryAt - now > delay) delay = retryAt - now;
        return delay;
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (GrainContext.GrainInstance is not DurableGrain and not IMessagingGrain)
        {
            throw new InvalidOperationException("Inbox activation requires IMessagingGrain or DurableGrain.");
        }

        if (CompactDeadLetters(TimeProvider.GetUtcNow()))
        {
            await PersistAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Releases the reported depth once the activation's messaging state is discarded.</summary>
    internal void ReleaseDepth() => ReportDepth(-_messages.Count);

    private void ReportDepth(int delta) => Shared.Instruments.OnInboxDepthChanged(Shared.GrainType, delta);

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
                owner.StageCompletion(entry.Message.Envelope.MessageId, deadLetter: null);
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

    [LoggerMessage(Level = LogLevel.Error, EventName = "DeliveryOperationFailed",
        Message = "Inbox delivery of message {MessageId} to {GrainId} failed")]
    private static partial void LogDeliveryOperationFailed(ILogger logger, Exception exception, MessageId messageId, GrainId grainId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Inbox command staging failed.")]
    private static partial void LogStagingFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No inbox handler registered for message {MessageId} to {ReceiverId}")]
    private static partial void LogHandlerNotFound(ILogger logger, MessageId messageId, GrainId receiverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Accepted message {MessageId} to {ReceiverId}")]
    private static partial void LogMessageAccepted(ILogger logger, MessageId messageId, GrainId receiverId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handler threw exception for message {MessageId} to {ReceiverId}")]
    private static partial void LogHandlerException(ILogger logger, Exception exception, MessageId messageId, GrainId receiverId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Inbox message sink dead-lettered message {MessageId} to {ReceiverId}: {Reason}")]
    private static partial void LogSinkDeadLettered(ILogger logger, Exception? exception, MessageId messageId, GrainId receiverId, string reason);
}

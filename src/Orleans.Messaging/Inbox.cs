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
                // The sender may retire its copy once this returns, so the acceptance or the processed record must be
                // durable. A completion staged by a handler or a sink can still be in flight.
                if (pending is null ? !AreCompletionsDurable : !IsDurable(pending)) await PersistForCallerAsync(cancellationToken).ConfigureAwait(true);
                if (pending is not null) _pump.Signal();

                Shared.Instruments.OnInboxMessageReceived(Shared.GrainType, "duplicate");

                // A sender whose answer was lost delivers again, so it receives the rejection which a sink first answered.
                return pending is null && _rejections.TryGetValue(key, out var rejection)
                    ? DeliveryResult.DeadLettered(rejection.Reason, rejection.Code)
                    : DeliveryResult.Duplicate();
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
    /// it fails the send. A message without
    /// <see cref="Envelope.DeliverAt"/> still occupies capacity for deliveries from other grains until it completes,
    /// and processing waits for a handler to be registered.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The inbox holds a different pending command with the same ID, or an inbox sink backpressured the message or
    /// reported an unsupported status.
    /// </exception>
    internal void StageLocal(InboxMessage message)
    {
        if (IsDuplicate(message, out _))
        {
            Shared.Instruments.OnInboxMessageReceived(Shared.GrainType, "duplicate");
            return;
        }

        if (StageSinkAcceptance(message) is { } sinkResult)
        {
            if (sinkResult.Status == DeliveryStatus.Backpressured)
            {
                throw new InvalidOperationException(
                    $"An inbox message sink backpressured message '{message.Envelope.MessageId}', which the grain sent to itself.");
            }

            return;
        }

        try
        {
            Emit(new() { Kind = InboxCommandKind.Accept, Message = message });
        }
        catch (Exception exception)
        {
            LogStagingFailed(Logger, exception);
            _pump.Fail(exception, "Inbox staging failed.");
            throw;
        }

        Shared.Instruments.OnInboxMessageReceived(Shared.GrainType, "accepted");
        LogMessageAccepted(Logger, message.Envelope.MessageId, GrainContext.GrainId);
    }

    /// <summary>
    /// Offers a message to the registered sinks and stages the outcome which the first sink to take the message reports.
    /// </summary>
    /// <returns>The delivery result, or <see langword="null"/> when every sink declines the message.</returns>
    /// <remarks>
    /// An accepted message journals only its processed record, in the write which commits the sink's staged changes.
    /// A duplicate or backpressured message stages nothing. A sink which dead-letters the message stages nothing itself,
    /// so the message is dead-lettered with its payload and recorded as processed. A sink exception, or an unsupported
    /// status, leaves the outcome unknown: it propagates to the caller with nothing staged, so the sender retries.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A sink reported an unsupported delivery status.</exception>
    private DeliveryResult? StageSinkAcceptance(InboxMessage message)
    {
        var key = message.Envelope.MessageId;
        foreach (var sink in _sinks)
        {
            if (!sink.TryAccept(message, out var result)) continue;
            if (result.Status is DeliveryStatus.Duplicate or DeliveryStatus.Backpressured)
            {
                Shared.Instruments.OnInboxMessageReceived(Shared.GrainType, result.Status == DeliveryStatus.Duplicate ? "duplicate" : "backpressured");
                return result;
            }

            if (result.Status is not (DeliveryStatus.Accepted or DeliveryStatus.DeadLettered))
            {
                throw new InvalidOperationException($"The inbox message sink reported the unsupported delivery status '{result.Status}'.");
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
                    LogSinkDeadLettered(Logger, key, GrainContext.GrainId, reason, result.Code);
                    StageDeadLetter(message, reason, attemptCount: 1, result.Code, new() { Reason = reason, Code = result.Code });
                }
            }
            catch (Exception exception)
            {
                // The sink's staged changes cannot commit without the processed record.
                LogStagingFailed(Logger, exception);
                _pump.Fail(exception, "Inbox staging failed.");
                throw;
            }

            Shared.Instruments.OnInboxMessageReceived(Shared.GrainType, accepted ? "accepted" : "dead_lettered");
            Shared.Instruments.OnInboxMessageProcessed(Shared.GrainType, accepted ? "success" : "dead_lettered");
            if (!accepted) return DeliveryResult.DeadLettered(reason, result.Code);
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
            // Retry deadlines follow the Durable Jobs clock, which schedules the wake-up for the next attempt.
            var attempts = checked(entry.State.AttemptCount + 1);
            var now = Shared.JobTimeProvider.GetUtcNow();
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

    private void StageCompletion(MessageId messageId, InboxDeadLetter? deadLetter, InboxRejection? rejection = null) =>
        Emit(new()
        {
            Kind = InboxCommandKind.Complete,
            MessageId = messageId,
            ProcessedAt = TimeProvider.GetUtcNow(),
            DeadLetter = deadLetter,
            Rejection = rejection
        });

    private void StageDeadLetter(InboxMessage message, string reason, int attemptCount, string? code = null, InboxRejection? rejection = null)
    {
        var now = TimeProvider.GetUtcNow();
        var key = message.Envelope.MessageId;
        CompactDeadLetters(now, reservedCapacity: _deadLetters.ContainsKey(key) ? 0 : 1);
        StageCompletion(key, new InboxDeadLetter
        {
            Message = message,
            DeadLetteredAt = now,
            Reason = reason,
            AttemptCount = attemptCount,
            Code = code
        }, rejection);
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
    /// nor any work source holds work which this activation can perform. Messages wait without a wake-up until a handler
    /// is registered.
    /// </summary>
    /// <param name="jobNow">The current Durable Jobs time, which the caller uses to schedule the wake-up.</param>
    internal TimeSpan? GetNextWakeupDelay(DateTimeOffset jobNow)
    {
        var next = GetWorkSourceDelay();
        if (_messages.Count == 0 || _handlers.IsEmpty || next <= TimeSpan.Zero) return next;
        foreach (var entry in _messages.Values)
        {
            var delay = GetDelay(entry, jobNow);
            if (delay == TimeSpan.Zero) return delay;
            if (next is null || delay < next) next = delay;
        }

        return next;
    }

    // Work sources may report any non-positive delay to request a prompt wake-up; it is normalized to zero so that due
    // times derived from it stay representable.
    private TimeSpan? GetWorkSourceDelay()
    {
        TimeSpan? next = null;
        foreach (var source in _workSources)
        {
            if (source.GetWakeupDelay() is not { } delay) continue;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox message sink dead-lettered message {MessageId} to {ReceiverId} with code {Code}: {Reason}")]
    private static partial void LogSinkDeadLettered(ILogger logger, MessageId messageId, GrainId receiverId, string reason, string? code);
}

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
/// Accepted messages are processed only after their acceptance is durable.
/// </remarks>
internal sealed partial class Inbox : IInbox, IInboxExtension, IStateMachine, IDurableValueCommandHandler<InboxCommand>
{
    private readonly MessagingPump _pump;
    private readonly InboxDispatcher _handlers = new([]);

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

    internal IReadOnlyDictionary<HierarchicalKey, InboxEntry> MessageState => _messages;

    internal IReadOnlyDictionary<HierarchicalKey, InboxDeadLetter> DeadLetterState => _deadLetters;

    internal InboxDispatcher Handlers => _handlers;

    public IInbox Register(IInboxHandler handler)
    {
        _handlers.Add(handler);
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
            if (_processed.TryGetValue(key, out var processedAt)
                && !MessagingTime.IsExpired(TimeProvider.GetUtcNow(), processedAt, Options.DeduplicationWindow))
            {
                Shared.Instruments.OnInboxMessageReceived(Shared.GrainType, "duplicate");
                return DeliveryResult.Duplicate();
            }

            if (_messages.TryGetValue(key, out var pending))
            {
                if (!EnvelopeEquivalence.AreSameCommand(pending.Message, message))
                {
                    throw new InvalidOperationException(
                        $"The inbox already contains a different command with message ID '{key}'.");
                }

                // The sender may retire its copy once this returns, so the acceptance must be durable.
                if (!IsDurable(pending)) await PersistAsync().ConfigureAwait(true);
                _pump.Signal();
                Shared.Instruments.OnInboxMessageReceived(Shared.GrainType, "duplicate");
                return DeliveryResult.Duplicate();
            }

            if (_messages.Count >= Options.MaxCapacity)
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
            await PersistAsync().ConfigureAwait(true);
            Shared.Instruments.OnInboxMessageReceived(Shared.GrainType, "accepted");
            LogMessageAccepted(Logger, key, GrainContext.GrainId);
            return DeliveryResult.Accepted();
        }
        catch (Exception exception)
        {
            LogDeliveryOperationFailed(Logger, exception, key, GrainContext.GrainId);
            throw;
        }
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
        var batch = new List<InboxEntry>(Math.Min(Options.InboxBatchSize, _messages.Count));
        foreach (var entry in _messages.Values)
        {
            if (IsDurable(entry) && (entry.State.NextAttemptAt is not { } at || at <= now))
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

            var attempts = checked(entry.State.AttemptCount + 1);
            if (attempts >= Options.MaxProcessingAttempts)
            {
                StageDeadLetter(entry, error.Message, attempts);
                status = "dead_lettered";
            }
            else
            {
                Emit(new()
                {
                    Kind = InboxCommandKind.Retry,
                    MessageId = entry.Message.Envelope.MessageId,
                    State = new()
                    {
                        AttemptCount = attempts,
                        LastError = error.ToString(),
                        NextAttemptAt = MessagingTime.AddClamped(TimeProvider.GetUtcNow(),
                            TimeSpan.FromTicks(Options.BackpressureRetryDelay.Ticks * (1L << Math.Min(attempts - 1, DurableMessagingOptions.MaximumBackoffExponent))))
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

    private void StageCompletion(InboxEntry entry, InboxDeadLetter? deadLetter) =>
        Emit(new()
        {
            Kind = InboxCommandKind.Complete,
            MessageId = entry.Message.Envelope.MessageId,
            ProcessedAt = TimeProvider.GetUtcNow(),
            DeadLetter = deadLetter
        });

    private void StageDeadLetter(InboxEntry entry, string reason, int attemptCount)
    {
        var now = TimeProvider.GetUtcNow();
        var key = entry.Message.Envelope.MessageId;
        CompactDeadLetters(now, reservedCapacity: _deadLetters.ContainsKey(key) ? 0 : 1);
        StageCompletion(entry, new InboxDeadLetter
        {
            Message = entry.Message,
            DeadLetteredAt = now,
            Reason = reason,
            AttemptCount = attemptCount
        });
    }

    private bool CompactDeadLetters(DateTimeOffset now, int reservedCapacity = 0)
    {
        if (_deadLetters.Count == 0) return false;
        var retained = new Dictionary<HierarchicalKey, InboxDeadLetter>(_deadLetters);
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

    internal bool RemoveDeadLetter(HierarchicalKey messageId)
    {
        _pump.ShutdownToken.ThrowIfCancellationRequested();
        if (!_deadLetters.ContainsKey(messageId)) return false;
        Emit(new() { Kind = InboxCommandKind.RemoveDeadLetters, RemovedKeys = [messageId] });
        return true;
    }

    /// <summary>
    /// Returns the delay until the next message is due, or <see langword="null"/> when the inbox is empty.
    /// </summary>
    internal TimeSpan? GetNextWakeupDelay()
    {
        if (_messages.Count == 0) return null;
        var now = TimeProvider.GetUtcNow();
        var next = DateTimeOffset.MaxValue;
        foreach (var entry in _messages.Values)
        {
            if (entry.State.NextAttemptAt is not { } at || at <= now) return TimeSpan.Zero;
            if (at < next) next = at;
        }

        return next - now;
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

    private async ValueTask PersistAsync()
    {
        try
        {
            await _pump.StateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (JournaledStatePostCommitException)
        {
            throw;
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
                owner.StageCompletion(entry, deadLetter: null);
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
                owner.StageDeadLetter(entry, reason, checked(entry.State.AttemptCount + 1));
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
    private static partial void LogDeliveryOperationFailed(ILogger logger, Exception exception, HierarchicalKey messageId, GrainId grainId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No inbox handler registered for message {MessageId} to {ReceiverId}")]
    private static partial void LogHandlerNotFound(ILogger logger, HierarchicalKey messageId, GrainId receiverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Accepted message {MessageId} to {ReceiverId}")]
    private static partial void LogMessageAccepted(ILogger logger, HierarchicalKey messageId, GrainId receiverId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handler threw exception for message {MessageId} to {ReceiverId}")]
    private static partial void LogHandlerException(ILogger logger, Exception exception, HierarchicalKey messageId, GrainId receiverId);
}

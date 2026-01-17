using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.DurableJobs;
using Orleans.Runtime;
using Orleans.Serialization.Session;

namespace Orleans.Journaling.Messaging;

/// <summary>
/// Processing pump for durable inbox messages.
/// Integrates with Orleans.DurableJobs to drive message processing as a per-grain background task.
/// </summary>
/// <remarks>
/// <para>
/// The pump operates in a trigger-based model: when messages are delivered to the inbox,
/// a DurableJob is scheduled to process pending messages. The pump iterates all pending
/// messages and invokes registered handlers for each route key.
/// </para>
/// <para>
/// Handler execution follows these principles:
/// <list type="bullet">
/// <item><term>Success</term><description>Remove from inbox, mark as processed</description></item>
/// <item><term>No Handler</term><description>Remove from inbox, mark as processed, log warning</description></item>
/// <item><term>Exception</term><description>Log error, mark as processed to prevent infinite retry (configurable)</description></item>
/// </list>
/// </para>
/// <para>
/// The pump automatically reschedules itself if messages remain after a processing cycle.
/// Messages are processed one at a time in dictionary iteration order (no ordering guarantees).
/// </para>
/// </remarks>
internal sealed class InboxProcessingPump : IDurableJobHandler
{
    /// <summary>
    /// The job name used for pump invocations.
    /// </summary>
    private const string PumpJobName = "inbox-processing-pump";

    private readonly ILocalDurableJobManager _jobManager;
    private readonly IDurableInbox _inbox;
    private readonly IDurableOutbox _outbox;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> _processed;
    private readonly IStateMachineManager _stateMachineManager;
    private readonly SerializerSessionPool _sessionPool;
    private readonly ILogger<InboxProcessingPump> _logger;
    private readonly GrainId _grainId;
    private readonly bool _removeOnHandlerException;

    /// <summary>
    /// Creates a new inbox processing pump instance.
    /// </summary>
    /// <param name="jobManager">Durable job manager for scheduling pump jobs.</param>
    /// <param name="inbox">The inbox containing pending messages and registered handlers.</param>
    /// <param name="outbox">The outbox for sending messages from handlers.</param>
    /// <param name="processed">Dictionary tracking processed messages for deduplication.</param>
    /// <param name="stateMachineManager">State machine manager for atomic persistence.</param>
    /// <param name="sessionPool">Serializer session pool for handler context.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="grainId">The grain ID of the grain owning this inbox.</param>
    /// <param name="removeOnHandlerException">Whether to remove messages from inbox when handler throws (default: true).</param>
    public InboxProcessingPump(
        ILocalDurableJobManager jobManager,
        IDurableInbox inbox,
        IDurableOutbox outbox,
        IDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> processed,
        IStateMachineManager stateMachineManager,
        SerializerSessionPool sessionPool,
        ILogger<InboxProcessingPump> logger,
        GrainId grainId,
        bool removeOnHandlerException = true)
    {
        ArgumentNullException.ThrowIfNull(jobManager);
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(processed);
        ArgumentNullException.ThrowIfNull(stateMachineManager);
        ArgumentNullException.ThrowIfNull(sessionPool);
        ArgumentNullException.ThrowIfNull(logger);

        _jobManager = jobManager;
        _inbox = inbox;
        _outbox = outbox;
        _processed = processed;
        _stateMachineManager = stateMachineManager;
        _sessionPool = sessionPool;
        _logger = logger;
        _grainId = grainId;
        _removeOnHandlerException = removeOnHandlerException;
    }

    /// <summary>
    /// Schedules the pump to run immediately if there are pending messages.
    /// Should be called when new messages are delivered to the inbox.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scheduled job, or null if the inbox is empty.</returns>
    public async Task<DurableJob?> SchedulePumpAsync(CancellationToken cancellationToken = default)
    {
        if (_inbox.Count == 0)
        {
            _logger.LogDebug("Inbox is empty for grain {GrainId}, skipping pump scheduling", _grainId);
            return null;
        }

        _logger.LogDebug(
            "Scheduling inbox processing pump for grain {GrainId} with {Count} pending messages",
            _grainId,
            _inbox.Count);

        // Schedule immediately
        var job = await _jobManager.ScheduleJobAsync(
            _grainId,
            PumpJobName,
            DateTimeOffset.UtcNow,
            metadata: null,
            cancellationToken).ConfigureAwait(true);

        return job;
    }

    /// <summary>
    /// Executes the inbox processing pump job.
    /// Called by the DurableJobs infrastructure when the pump job is due.
    /// </summary>
    /// <param name="context">The job execution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ExecuteJobAsync(IDurableJobContext context, CancellationToken cancellationToken)
    {
        if (context.Job.Name != PumpJobName)
        {
            _logger.LogWarning(
                "Unexpected job name '{JobName}' in InboxProcessingPump for grain {GrainId}",
                context.Job.Name,
                _grainId);
            return;
        }

        _logger.LogInformation(
            "Starting inbox processing pump for grain {GrainId} with {Count} pending messages (attempt {AttemptNumber})",
            _grainId,
            _inbox.Count,
            context.DequeueCount);

        // Snapshot pending messages to avoid collection modification during iteration
        var pending = _inbox.Messages.ToList();
        var processedCount = 0;
        var noHandlerCount = 0;
        var failedCount = 0;

        foreach (var envelope in pending)
        {
            try
            {
                var result = await ProcessMessageAsync(envelope, cancellationToken).ConfigureAwait(true);

                switch (result)
                {
                    case ProcessingResult.Success:
                        processedCount++;
                        _logger.LogInformation(
                            "Successfully processed message {MessageId} from {SenderId} on route '{RouteKey}'",
                            envelope.MessageId,
                            envelope.SenderId,
                            envelope.RouteKey);
                        break;

                    case ProcessingResult.NoHandler:
                        noHandlerCount++;
                        _logger.LogWarning(
                            "No handler registered for route '{RouteKey}', removing message {MessageId} from {SenderId}",
                            envelope.RouteKey,
                            envelope.MessageId,
                            envelope.SenderId);
                        break;

                    case ProcessingResult.HandlerException:
                        failedCount++;
                        _logger.LogWarning(
                            "Handler threw exception for message {MessageId} from {SenderId} on route '{RouteKey}', message {Action}",
                            envelope.MessageId,
                            envelope.SenderId,
                            envelope.RouteKey,
                            _removeOnHandlerException ? "removed from inbox" : "will retry");
                        break;

                    case ProcessingResult.AlreadyProcessed:
                        // Already processed by concurrent invocation, skip
                        break;
                }
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(
                    ex,
                    "Error processing message {MessageId} from {SenderId} on route '{RouteKey}'",
                    envelope.MessageId,
                    envelope.SenderId,
                    envelope.RouteKey);

                // On unexpected exception, mark as processed to avoid infinite retry
                var key = (envelope.SenderId, envelope.MessageId);
                if (_inbox.RemoveMessage(envelope.SenderId, envelope.MessageId))
                {
                    _processed[key] = DateTimeOffset.UtcNow;
                }
            }
        }

        // Persist inbox and processed state atomically
        await _stateMachineManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);

        _logger.LogInformation(
            "Completed inbox processing pump for grain {GrainId}: {ProcessedCount} processed, {NoHandlerCount} no handler, {FailedCount} failed, {RemainingCount} remaining",
            _grainId,
            processedCount,
            noHandlerCount,
            failedCount,
            _inbox.Count);

        // Reschedule pump if messages remain
        if (_inbox.Count > 0)
        {
            _logger.LogInformation(
                "Rescheduling inbox processing pump for grain {GrainId} with {RemainingCount} pending messages",
                _grainId,
                _inbox.Count);

            await _jobManager.ScheduleJobAsync(
                _grainId,
                PumpJobName,
                DateTimeOffset.UtcNow,
                metadata: null,
                cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Processes a single message by invoking its handler.
    /// </summary>
    /// <param name="envelope">The message envelope to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The processing result.</returns>
    private async ValueTask<ProcessingResult> ProcessMessageAsync(DurableEnvelope envelope, CancellationToken cancellationToken)
    {
        var key = (envelope.SenderId, envelope.MessageId);

        // Check if already processed (concurrent processing guard)
        if (_inbox.ContainsOrProcessed(envelope.SenderId, envelope.MessageId) && !_inbox.TryGetMessage(envelope.SenderId, envelope.MessageId, out _))
        {
            return ProcessingResult.AlreadyProcessed;
        }

        // Check if handler exists
        if (!_inbox.HasHandler(envelope.RouteKey))
        {
            // Remove from inbox and mark as processed
            _inbox.RemoveMessage(envelope.SenderId, envelope.MessageId);
            _processed[key] = DateTimeOffset.UtcNow;
            return ProcessingResult.NoHandler;
        }

        try
        {
            // Create handler context with the actual outbox
            var context = new InboxHandlerContext(envelope, _grainId, _outbox, _sessionPool);

            // Get handler (we already checked it exists)
            var handler = GetHandler(envelope.RouteKey);

            // Invoke handler
            await handler.HandleAsync(context, cancellationToken).ConfigureAwait(true);

            // Mark as processed
            _inbox.RemoveMessage(envelope.SenderId, envelope.MessageId);
            _processed[key] = DateTimeOffset.UtcNow;

            return ProcessingResult.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Handler threw exception for message {MessageId} from {SenderId} on route '{RouteKey}'",
                envelope.MessageId,
                envelope.SenderId,
                envelope.RouteKey);

            if (_removeOnHandlerException)
            {
                // Mark as processed to avoid infinite retry
                _inbox.RemoveMessage(envelope.SenderId, envelope.MessageId);
                _processed[key] = DateTimeOffset.UtcNow;
            }

            return ProcessingResult.HandlerException;
        }
    }

    /// <summary>
    /// Gets the handler for a route key.
    /// </summary>
    /// <remarks>
    /// This method assumes HasHandler() was called first to verify the handler exists.
    /// If the handler is not found, throws InvalidOperationException.
    /// </remarks>
    private IInboxHandler GetHandler(string routeKey)
    {
        if (_inbox.TryGetHandler(routeKey, out var handler))
        {
            return handler;
        }

        throw new InvalidOperationException($"Handler for route '{routeKey}' not found, but HasHandler returned true");
    }

    /// <summary>
    /// Result of processing a message.
    /// </summary>
    private enum ProcessingResult
    {
        /// <summary>
        /// Message was successfully processed by handler.
        /// </summary>
        Success,

        /// <summary>
        /// No handler registered for the route key.
        /// </summary>
        NoHandler,

        /// <summary>
        /// Handler threw an exception during processing.
        /// </summary>
        HandlerException,

        /// <summary>
        /// Message was already processed (concurrent processing guard).
        /// </summary>
        AlreadyProcessed
    }
}

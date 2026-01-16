using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.DurableJobs;
using Orleans.Runtime;

namespace Orleans.Journaling.Messaging;

/// <summary>
/// Delivery pump for durable outbox messages.
/// Integrates with Orleans.DurableJobs to drive message delivery as a per-grain background task.
/// </summary>
/// <remarks>
/// <para>
/// The pump operates in a trigger-based model: when messages are added to the outbox,
/// a DurableJob is scheduled to process pending messages. The pump iterates all pending
/// messages and attempts delivery to their target grains via <see cref="IDurableInboxExtension.DeliverAsync"/>.
/// </para>
/// <para>
/// Delivery results are handled as follows:
/// <list type="bullet">
/// <item><term>Accepted</term><description>Message persisted to inbox, remove from outbox</description></item>
/// <item><term>Duplicate</term><description>Already processed by target, remove from outbox</description></item>
/// <item><term>Backpressured</term><description>Inbox at capacity, retry with exponential backoff</description></item>
/// <item><term>RouteNotFound</term><description>No handler for route, log warning and remove from outbox</description></item>
/// </list>
/// </para>
/// <para>
/// The pump automatically reschedules itself if messages remain after a delivery cycle.
/// Exponential backoff is applied when encountering backpressure, starting at 1 second
/// and doubling up to 60 seconds.
/// </para>
/// </remarks>
internal sealed class OutboxDeliveryPump : IDurableJobHandler
{
    /// <summary>
    /// The job name used for pump invocations.
    /// </summary>
    private const string PumpJobName = "outbox-delivery-pump";

    /// <summary>
    /// Initial backoff delay when encountering backpressure (1 second).
    /// </summary>
    private static readonly TimeSpan InitialBackoffDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum backoff delay when encountering backpressure (60 seconds).
    /// </summary>
    private static readonly TimeSpan MaxBackoffDelay = TimeSpan.FromSeconds(60);

    private readonly IGrainFactory _grainFactory;
    private readonly ILocalDurableJobManager _jobManager;
    private readonly IDurableOutbox _outbox;
    private readonly IStateMachineManager _stateMachineManager;
    private readonly ILogger<OutboxDeliveryPump> _logger;
    private readonly GrainId _grainId;

    // Track backoff state per target grain for exponential backoff
    private readonly Dictionary<GrainId, BackoffState> _backoffStates = new();

    /// <summary>
    /// Creates a new outbox delivery pump instance.
    /// </summary>
    /// <param name="grainFactory">Grain factory for accessing target grains.</param>
    /// <param name="jobManager">Durable job manager for scheduling pump jobs.</param>
    /// <param name="outbox">The outbox containing pending messages.</param>
    /// <param name="stateMachineManager">State machine manager for atomic persistence.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="grainId">The grain ID of the grain owning this outbox.</param>
    public OutboxDeliveryPump(
        IGrainFactory grainFactory,
        ILocalDurableJobManager jobManager,
        IDurableOutbox outbox,
        IStateMachineManager stateMachineManager,
        ILogger<OutboxDeliveryPump> logger,
        GrainId grainId)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        ArgumentNullException.ThrowIfNull(jobManager);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(stateMachineManager);
        ArgumentNullException.ThrowIfNull(logger);

        _grainFactory = grainFactory;
        _jobManager = jobManager;
        _outbox = outbox;
        _stateMachineManager = stateMachineManager;
        _logger = logger;
        _grainId = grainId;
    }

    /// <summary>
    /// Schedules the pump to run immediately if there are pending messages.
    /// Should be called when new messages are added to the outbox.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scheduled job, or null if the outbox is empty.</returns>
    public async Task<DurableJob?> SchedulePumpAsync(CancellationToken cancellationToken = default)
    {
        if (_outbox.Count == 0)
        {
            _logger.LogDebug("Outbox is empty for grain {GrainId}, skipping pump scheduling", _grainId);
            return null;
        }

        _logger.LogDebug(
            "Scheduling outbox delivery pump for grain {GrainId} with {Count} pending messages",
            _grainId,
            _outbox.Count);

        // Schedule immediately
        var job = await _jobManager.ScheduleJobAsync(
            _grainId,
            PumpJobName,
            DateTimeOffset.UtcNow,
            metadata: null,
            cancellationToken).ConfigureAwait(false);

        return job;
    }

    /// <summary>
    /// Executes the outbox delivery pump job.
    /// Called by the DurableJobs infrastructure when the pump job is due.
    /// </summary>
    /// <param name="context">The job execution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ExecuteJobAsync(IDurableJobContext context, CancellationToken cancellationToken)
    {
        if (context.Job.Name != PumpJobName)
        {
            _logger.LogWarning(
                "Unexpected job name '{JobName}' in OutboxDeliveryPump for grain {GrainId}",
                context.Job.Name,
                _grainId);
            return;
        }

        _logger.LogInformation(
            "Starting outbox delivery pump for grain {GrainId} with {Count} pending messages (attempt {AttemptNumber})",
            _grainId,
            _outbox.Count,
            context.DequeueCount);

        // Snapshot pending messages to avoid collection modification during iteration
        var pending = _outbox.Messages.ToList();
        var deliveredCount = 0;
        var backpressuredCount = 0;
        var routeNotFoundCount = 0;
        var failedCount = 0;

        foreach (var envelope in pending)
        {
            try
            {
                var result = await DeliverMessageAsync(envelope, cancellationToken).ConfigureAwait(false);

                switch (result.Status)
                {
                    case DeliveryStatus.Accepted:
                    case DeliveryStatus.Duplicate:
                        // Success - remove from outbox
                        _outbox.RemoveMessage(envelope.MessageId);
                        deliveredCount++;

                        // Clear backoff state for this target
                        _backoffStates.Remove(envelope.ReceiverId);

                        _logger.LogInformation(
                            "Successfully delivered message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}' (Status: {Status})",
                            envelope.MessageId,
                            envelope.SenderId,
                            envelope.ReceiverId,
                            envelope.RouteKey,
                            result.Status);
                        break;

                    case DeliveryStatus.Backpressured:
                        // Target inbox at capacity - will retry with backoff
                        backpressuredCount++;
                        UpdateBackoffState(envelope.ReceiverId);

                        _logger.LogWarning(
                            "Backpressured delivering message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}', will retry",
                            envelope.MessageId,
                            envelope.SenderId,
                            envelope.ReceiverId,
                            envelope.RouteKey);
                        break;

                    case DeliveryStatus.RouteNotFound:
                        // No handler for route - remove from outbox (cannot be delivered)
                        _outbox.RemoveMessage(envelope.MessageId);
                        routeNotFoundCount++;

                        _logger.LogWarning(
                            "Route not found for message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}', removing from outbox: {Message}",
                            envelope.MessageId,
                            envelope.SenderId,
                            envelope.ReceiverId,
                            envelope.RouteKey,
                            result.Message ?? "(no message)");
                        break;

                    default:
                        _logger.LogWarning(
                            "Unexpected delivery status {Status} for message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}'",
                            result.Status,
                            envelope.MessageId,
                            envelope.SenderId,
                            envelope.ReceiverId,
                            envelope.RouteKey);
                        break;
                }
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(
                    ex,
                    "Error delivering message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}'",
                    envelope.MessageId,
                    envelope.SenderId,
                    envelope.ReceiverId,
                    envelope.RouteKey);

                // On exception, update backoff state to slow down retries
                UpdateBackoffState(envelope.ReceiverId);
            }
        }

        // Persist outbox changes atomically
        await _stateMachineManager.WriteStateAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Completed outbox delivery pump for grain {GrainId}: {DeliveredCount} delivered, {BackpressuredCount} backpressured, {RouteNotFoundCount} route not found, {FailedCount} failed, {RemainingCount} remaining",
            _grainId,
            deliveredCount,
            backpressuredCount,
            routeNotFoundCount,
            failedCount,
            _outbox.Count);

        // Reschedule pump if messages remain
        if (_outbox.Count > 0)
        {
            var nextDelay = CalculateNextDelay();

            _logger.LogInformation(
                "Rescheduling outbox delivery pump for grain {GrainId} in {Delay} with {RemainingCount} pending messages",
                _grainId,
                nextDelay,
                _outbox.Count);

            await _jobManager.ScheduleJobAsync(
                _grainId,
                PumpJobName,
                DateTimeOffset.UtcNow.Add(nextDelay),
                metadata: null,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Attempts to deliver a single message to its target inbox.
    /// </summary>
    private async ValueTask<DeliveryResult> DeliverMessageAsync(DurableEnvelope envelope, CancellationToken cancellationToken)
    {
        // Get the target grain's inbox extension
        var targetGrain = _grainFactory.GetGrain<IDurableInboxExtension>(envelope.ReceiverId);

        // Deliver with no long-polling (immediate return after persistence)
        var options = new DeliveryOptions { PollTimeout = TimeSpan.Zero };

        var result = await targetGrain.DeliverAsync(envelope, options, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Updates backoff state for a target grain that experienced backpressure or failure.
    /// </summary>
    private void UpdateBackoffState(GrainId targetGrainId)
    {
        if (!_backoffStates.TryGetValue(targetGrainId, out var state))
        {
            state = new BackoffState { ConsecutiveFailures = 0, CurrentDelay = InitialBackoffDelay };
        }

        state.ConsecutiveFailures++;
        state.CurrentDelay = TimeSpan.FromSeconds(Math.Min(
            state.CurrentDelay.TotalSeconds * 2,
            MaxBackoffDelay.TotalSeconds));

        _backoffStates[targetGrainId] = state;
    }

    /// <summary>
    /// Calculates the next delay for pump rescheduling based on backoff states.
    /// Uses the maximum backoff delay among all pending targets.
    /// </summary>
    private TimeSpan CalculateNextDelay()
    {
        if (_backoffStates.Count == 0)
        {
            // No backoff needed, schedule immediately
            return TimeSpan.Zero;
        }

        // Use the maximum backoff delay to avoid overwhelming targets with backpressure
        var maxDelay = _backoffStates.Values.Max(s => s.CurrentDelay);
        return maxDelay;
    }

    /// <summary>
    /// Tracks exponential backoff state for a target grain.
    /// </summary>
    private struct BackoffState
    {
        public int ConsecutiveFailures;
        public TimeSpan CurrentDelay;
    }
}

using System;

namespace Orleans.Messaging.Configuration;

/// <summary>
/// Configuration options for durable messaging inboxes, outboxes, and the messaging pump.
/// </summary>
/// <remarks>
/// <para>
/// These options control inbox capacity, deduplication tracking, inbox and outbox retry and
/// dead-letter behavior, and pump batch sizes.
/// </para>
/// <para>
/// Transport is at-least-once. Retained processed records and a durable admission floor deduplicate logical sends.
/// Configuration values affect delivery availability, memory usage, throughput, and recovery characteristics.
/// </para>
/// </remarks>
public class DurableMessagingOptions
{
    internal const int MaximumBackoffExponent = 6;
    internal const int MaximumBackoffMultiplier = 1 << MaximumBackoffExponent;

    // The runtime multiplies this base delay by at most MaximumBackoffMultiplier.
    // Keep the expanded delay within the timer implementation's uint-millisecond limit.
    private static readonly TimeSpan MaxSupportedRetryDelay =
        TimeSpan.FromTicks(
            TimeSpan.FromMilliseconds(uint.MaxValue - 1).Ticks
            / MaximumBackoffMultiplier);

    /// <summary>
    /// Gets or sets the maximum number of pending inbox messages.
    /// When this limit is reached, new deliveries return <c>DeliveryResult.Backpressured()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A lower value (e.g., 100) provides stronger backpressure but may reduce throughput.
    /// A higher value (e.g., 10,000) allows more buffering but increases memory usage and recovery time.
    /// </para>
    /// <para>
    /// The inbox capacity is checked before accepting new messages. Messages are persisted to durable
    /// storage, so capacity limits affect both in-memory state and storage I/O during recovery.
    /// </para>
    /// <para>
    /// A message with an <see cref="Envelope.DeliverAt"/> time counts toward this limit from its acceptance until it
    /// completes, so size the limit for the messages a grain holds scheduled as well as those it is processing.
    /// </para>
    /// <para>
    /// Messages a grain sends to itself are accepted regardless of this limit, because sending is synchronous. They count
    /// toward it until they complete, so they can cause deliveries from other grains to be backpressured.
    /// </para>
    /// </remarks>
    /// <value>
    /// The maximum inbox capacity. Must be greater than zero. Defaults to 1000.
    /// </value>
    public int MaxCapacity { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the retention interval used to advance the receiving inbox's durable admission floor.
    /// A tracked processed message is answered with <c>DeliveryResult.Duplicate()</c>, or, when an inbox
    /// sink rejected it on delivery, with the same <c>DeliveryResult.DeadLettered</c> reason and code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A longer interval (e.g., 30 days) retains more processed records and admits older sends, increasing
    /// memory usage and storage I/O. A shorter interval (e.g., 1 hour) fences older sends sooner.
    /// </para>
    /// <para>
    /// The receiver proposes an admission floor from its current clock minus this interval. The floor only advances,
    /// is journaled, and survives recovery and migration. A processed record becomes eligible for compaction once its
    /// immutable <see cref="InboxMessage.RedeliveryDeadline"/> is at or below that floor. Every subsequent untracked
    /// delivery with that cutoff is fenced, including after a backward clock jump. Null-cutoff records are retained
    /// permanently.
    /// </para>
    /// <para>
    /// Clock jumps and skew can cause early fencing or extended retention. Deduplication safety follows the journaled
    /// floor and fixed per-send cutoffs, independently of clock synchronization and of
    /// <see cref="OutboxRetryTimeout"/>. Size this interval for the delivery delays and clock variation the application
    /// expects to tolerate.
    /// </para>
    /// </remarks>
    /// <value>
    /// The retention interval. Must be greater than zero. Defaults to 7 days.
    /// </value>
    public TimeSpan DeduplicationWindow { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Gets or sets the delay before the first retry of a failed outbox delivery or inbox processing attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The outbox doubles this interval after each failed delivery attempt, including when the target inbox returns
    /// <c>DeliveryResult.Backpressured()</c>, up to 64 times this interval. The inbox applies the same backoff after a
    /// handler fails.
    /// </para>
    /// <para>
    /// A shorter delay (e.g., 100ms) enables faster recovery when the target processes messages quickly,
    /// but may increase CPU usage during sustained backpressure. A longer delay (e.g., 5 seconds)
    /// reduces retry overhead but increases latency for message delivery.
    /// </para>
    /// <para>
    /// For high-throughput scenarios where quick recovery from backpressure is important,
    /// consider values between 100-500ms. For less time-sensitive workloads, 1-5 seconds is appropriate.
    /// </para>
    /// </remarks>
    /// <value>
    /// The initial retry interval. Must be greater than zero. Defaults to 1 second.
    /// </value>
    public TimeSpan RetryInitialInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets how long the inbox retries a message whose handler attempts throw before the message is
    /// dead-lettered.
    /// </summary>
    /// <remarks>
    /// The timeout starts with the first failed attempt, and the last attempt runs when it elapses. A handler exception
    /// leaves the message's outcome unknown, so retries span transient faults, such as outages and deployments which
    /// fix a bug; a handler calls <c>IInboxHandlerContext.Fail</c> to dead-letter a message at once. A retrying message
    /// keeps its place in <see cref="MaxCapacity"/>.
    /// </remarks>
    /// <value>
    /// Must be greater than or equal to <see cref="TimeSpan.Zero"/>, which dead-letters a message at its first failure.
    /// Defaults to 6 hours.
    /// </value>
    public TimeSpan InboxRetryTimeout { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Gets or sets the absolute delivery-attempt budget from the original send before the outbox dead-letters
    /// the message, including while the receiver is backpressured or reports no inbox handler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The outbox journals the expiration and immutable admission cutoff when it stages the send. Backpressure and
    /// recovery preserve that budget. The timeout follows the Durable Jobs clock; a jump can end retries early or
    /// prolong them. A timeout or <see cref="DeliveryStatus.Expired"/> leaves the receiver's earlier delivery outcome
    /// unknown. A source's <see cref="IOutboxMessageSource.OnRetired"/> stages application responses atomically with
    /// retirement; see <see cref="OutboxMessageRetirementStatus.DeadLettered"/>.
    /// </para>
    /// <para>
    /// The outbox logs each <c>DeliveryResult.HandlerNotFound()</c> answer as an error.
    /// </para>
    /// </remarks>
    /// <value>Must be greater than zero. Defaults to 6 hours.</value>
    public TimeSpan OutboxRetryTimeout { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Gets or sets how long inbox and outbox dead letters are retained.
    /// </summary>
    public TimeSpan DeadLetterRetentionPeriod { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Gets or sets the maximum number of dead letters retained per inbox and per outbox.
    /// </summary>
    public int MaxRetainedDeadLetters { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the maximum number of inbox messages processed by one messaging pump pass.
    /// </summary>
    public int InboxBatchSize { get; set; } = 32;

    /// <summary>
    /// Gets or sets the maximum number of outbox delivery attempts in flight for one grain.
    /// </summary>
    /// <remarks>
    /// The pump starts another attempt as soon as one completes, so a slow receiver holds one slot without delaying
    /// messages to other receivers.
    /// </remarks>
    public int OutboxBatchSize { get; set; } = 32;

    /// <summary>
    /// Validates the configuration values and throws if any are invalid.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <see cref="MaxCapacity"/> is less than or equal to zero,
    /// or if <see cref="DeduplicationWindow"/> is less than or equal to <see cref="TimeSpan.Zero"/>,
    /// or if a retry, retention, or batch option is outside its supported range.
    /// </exception>

    /// <remarks>
    /// This method is typically called by the dependency injection container during service registration
    /// to ensure configuration values are valid before the system starts.
    /// </remarks>
    public void Validate()
    {

        if (MaxCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCapacity), MaxCapacity, "MaxCapacity must be greater than zero.");
        }

        if (DeduplicationWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(DeduplicationWindow), DeduplicationWindow, "DeduplicationWindow must be greater than TimeSpan.Zero.");
        }

        if (RetryInitialInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryInitialInterval), RetryInitialInterval, "RetryInitialInterval must be greater than TimeSpan.Zero.");
        }
        if (RetryInitialInterval > MaxSupportedRetryDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RetryInitialInterval),
                RetryInitialInterval,
                $"RetryInitialInterval must be less than or equal to {MaxSupportedRetryDelay}.");
        }

        if (InboxRetryTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(InboxRetryTimeout), InboxRetryTimeout, "InboxRetryTimeout must be greater than or equal to TimeSpan.Zero.");
        }

        if (OutboxRetryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(OutboxRetryTimeout), OutboxRetryTimeout, "OutboxRetryTimeout must be greater than TimeSpan.Zero.");
        }

        if (DeadLetterRetentionPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DeadLetterRetentionPeriod),
                DeadLetterRetentionPeriod,
                "DeadLetterRetentionPeriod must be greater than TimeSpan.Zero.");
        }

        if (MaxRetainedDeadLetters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRetainedDeadLetters),
                MaxRetainedDeadLetters,
                "MaxRetainedDeadLetters must be greater than zero.");
        }

        if (InboxBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InboxBatchSize), InboxBatchSize, "InboxBatchSize must be greater than zero.");
        }

        if (OutboxBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(OutboxBatchSize), OutboxBatchSize, "OutboxBatchSize must be greater than zero.");
        }
    }
}

using System;
using Polly;

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
/// Transport is at-least-once. Deduplication provides effectively-once handler effects only
/// while the processed-message record is retained. Configuration values affect memory usage,
/// throughput, and recovery characteristics.
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
    /// Gets or sets the maximum number of pending inbox messages without <see cref="Envelope.DeliverAt"/>.
    /// When this limit is reached, new deliveries without <see cref="Envelope.DeliverAt"/> return <c>DeliveryResult.Backpressured()</c>.
    /// The inbox admits scheduled messages regardless of this limit.
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
    /// Messages with an <see cref="Envelope.DeliverAt"/> time do not count toward this limit, including after they
    /// become due, and are accepted regardless of it.
    /// </para>
    /// </remarks>
    /// <value>
    /// The maximum inbox capacity. Must be greater than zero. Defaults to 1000.
    /// </value>
    public int MaxCapacity { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the time window for tracking processed messages to prevent duplicates.
    /// Messages that were processed within this window will be rejected with <c>DeliveryResult.Duplicate()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A longer window (e.g., 30 days) provides stronger deduplication guarantees but increases
    /// memory usage and storage I/O. A shorter window (e.g., 1 hour) reduces overhead but may
    /// allow duplicate processing if retries are delayed.
    /// </para>
    /// <para>
    /// Processed message tracking uses the exact message ID within the receiving inbox, with timestamps.
    /// Expired entries are removed atomically when a replay is accepted and are compacted when the
    /// inbox writes a snapshot.
    /// </para>
    /// <para>
    /// Consider your retry policies when setting this value. For example, if senders retry for
    /// up to 24 hours, set the window to at least 48 hours to ensure deduplication coverage.
    /// </para>
    /// </remarks>
    /// <value>
    /// The deduplication window. Must be greater than zero. Defaults to 7 days.
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
    /// Gets or sets the maximum number of attempts before an inbox message is dead-lettered.
    /// </summary>
    public int MaxProcessingAttempts { get; set; } = 5;

    /// <summary>
    /// Gets or sets how long the outbox retries a message whose delivery attempts fail, such as during an
    /// infrastructure outage or while the receiver reports no inbox handler, before the message is dead-lettered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The timeout starts with the first failed attempt and restarts whenever the receiver answers with
    /// <c>DeliveryResult.Backpressured()</c>, so the outbox retries a backpressured message until the receiver takes it.
    /// A receiver checks its deduplication records before answering, so only attempts since the timeout started can have
    /// been accepted, and the timeout stays within <see cref="DeduplicationWindow"/> to keep their retries deduplicated.
    /// </para>
    /// <para>
    /// The outbox logs each <c>DeliveryResult.HandlerNotFound()</c> answer as an error.
    /// </para>
    /// </remarks>
    /// <value>Must be greater than zero and less than <see cref="DeduplicationWindow"/>. Defaults to 6 hours.</value>
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
    /// Gets or sets the maximum number of outbox messages processed by one messaging pump pass.
    /// </summary>
    public int OutboxBatchSize { get; set; } = 32;

    /// <summary>
    /// Gets or sets the resilience pipeline which runs each outbox delivery attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The outbox calls each receiver through this pipeline, so its strategies, such as timeouts, retries, and circuit
    /// breakers, act within one delivery attempt. When an attempt fails, the outbox schedules the next attempt with
    /// <see cref="RetryInitialInterval"/> backoff and continues delivering other messages. Receiving inboxes
    /// deduplicate by message ID, so retrying a delivery whose outcome is unknown is safe.
    /// </para>
    /// <para>
    /// The outbox treats a receiver's answer as proof that no earlier call for the message is still in flight, so the
    /// pipeline makes one receiver call at a time. One pipeline instance serves every outbox delivery in the silo.
    /// </para>
    /// </remarks>
    /// <value>Defaults to <see cref="ResiliencePipeline.Empty"/>, which calls the receiver directly.</value>
    public ResiliencePipeline DeliveryResiliencePipeline { get; set; } = ResiliencePipeline.Empty;

    /// <summary>
    /// Validates the configuration values and throws if any are invalid.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <see cref="MaxCapacity"/> is less than or equal to zero,
    /// or if <see cref="DeduplicationWindow"/> is less than or equal to <see cref="TimeSpan.Zero"/>,
    /// or if a retry, retention, or batch option is outside its supported range.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown if <see cref="DeliveryResiliencePipeline"/> is <see langword="null"/>.</exception>
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

        if (MaxProcessingAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxProcessingAttempts), MaxProcessingAttempts, "MaxProcessingAttempts must be greater than zero.");
        }

        if (OutboxRetryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(OutboxRetryTimeout), OutboxRetryTimeout, "OutboxRetryTimeout must be greater than TimeSpan.Zero.");
        }

        if (OutboxRetryTimeout >= DeduplicationWindow)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OutboxRetryTimeout),
                OutboxRetryTimeout,
                "OutboxRetryTimeout must be less than DeduplicationWindow.");
        }

        if (DeliveryResiliencePipeline is null)
        {
            throw new ArgumentNullException(nameof(DeliveryResiliencePipeline));
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

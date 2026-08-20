using System;

namespace Orleans.DurableMessaging.Configuration;

/// <summary>
/// Configuration options for the durable inbox messaging system.
/// </summary>
/// <remarks>
/// <para>
/// These options control the behavior of the durable inbox, including capacity limits,
/// deduplication tracking, retry behavior, and pump batch sizes.
/// </para>
/// <para>
/// Transport is at-least-once. Deduplication provides effectively-once handler effects only
/// while the processed-message record is retained. Configuration values affect memory usage,
/// throughput, and recovery characteristics.
/// </para>
/// </remarks>
public class DurableInboxOptions
{
    /// <summary>
    /// Gets or sets the maximum number of pending messages in the inbox.
    /// When this limit is reached, new message deliveries will return <c>DeliveryResult.Backpressured()</c>.
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
    /// Processed message tracking uses composite key (SenderId, MessageId) with timestamps.
    /// Expired entries are removed atomically when a replay is accepted and are also eligible for
    /// compaction during inbox pump maintenance.
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
    /// Gets or sets the base delay between retry attempts when delivery encounters backpressure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the target inbox is at capacity and returns <c>DeliveryResult.Backpressured()</c>,
    /// the outbox delivery pump applies exponential backoff from this duration before retrying.
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
    /// The base backpressure retry delay. Must be greater than zero. Defaults to 1 second.
    /// </value>
    public TimeSpan BackpressureRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the maximum number of attempts before an inbox message is dead-lettered.
    /// </summary>
    public int MaxProcessingAttempts { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum number of attempts before an outbox message is dead-lettered.
    /// </summary>
    public int MaxDeliveryAttempts { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum age of an outbox message.
    /// </summary>
    public TimeSpan MaxOutboxRetryAge { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Gets or sets the maximum number of inbox messages processed by one durable job attempt.
    /// </summary>
    public int InboxBatchSize { get; set; } = 32;

    /// <summary>
    /// Gets or sets the maximum number of outbox messages processed by one durable job attempt.
    /// </summary>
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

        if (BackpressureRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(BackpressureRetryDelay), BackpressureRetryDelay, "BackpressureRetryDelay must be greater than TimeSpan.Zero.");
        }

        if (MaxProcessingAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxProcessingAttempts), MaxProcessingAttempts, "MaxProcessingAttempts must be greater than zero.");
        }

        if (MaxDeliveryAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDeliveryAttempts), MaxDeliveryAttempts, "MaxDeliveryAttempts must be greater than zero.");
        }

        if (MaxOutboxRetryAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxOutboxRetryAge), MaxOutboxRetryAge, "MaxOutboxRetryAge must be greater than TimeSpan.Zero.");
        }

        if (MaxOutboxRetryAge >= DeduplicationWindow)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxOutboxRetryAge),
                MaxOutboxRetryAge,
                "MaxOutboxRetryAge must be less than DeduplicationWindow.");
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

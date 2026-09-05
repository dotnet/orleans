using System;

namespace Orleans.DurableMessaging.Configuration;

/// <summary>
/// Configuration options for the durable inbox messaging system.
/// </summary>
/// <remarks>
/// <para>
/// These options control the behavior of the durable inbox, including capacity limits,
/// deduplication tracking, processing concurrency, and long-polling support.
/// </para>
/// <para>
/// The durable inbox provides exactly-once message delivery with backpressure signaling.
/// Configuration values affect memory usage, throughput, and recovery characteristics.
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
    /// Entries older than the deduplication window are eligible for compaction during snapshots.
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
    /// Gets or sets the maximum number of inbox messages to process concurrently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>DEPRECATED:</strong> This property is obsolete and no longer affects processing behavior.
    /// Orleans grains process messages sequentially to maintain the single-threaded grain model.
    /// </para>
    /// <para>
    /// Concurrent processing violates Orleans' fundamental grain semantics, which guarantee that:
    /// <list type="bullet">
    /// <item>Grains handle one request at a time (automatic synchronization)</item>
    /// <item>State modifications are serialized (no race conditions)</item>
    /// <item>Handlers see consistent state (predictable execution order)</item>
    /// </list>
    /// </para>
    /// <para>
    /// For high-throughput scenarios, scale horizontally by:
    /// <list type="bullet">
    /// <item>Partitioning work across multiple grain instances</item>
    /// <item>Using grain keys to distribute load (e.g., account-123, account-456)</item>
    /// <item>Avoiding sequential dependencies between messages when possible</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <value>
    /// This value is ignored. Messages are always processed sequentially. Defaults to 1.
    /// </value>
    [Obsolete("Concurrent message processing violates Orleans' single-threaded grain model. This property is ignored; messages are always processed sequentially.")]
    public int ProcessingConcurrency { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether long-polling is enabled for message delivery.
    /// When enabled, <c>DeliverAsync</c> will wait up to <c>DeliveryOptions.PollTimeout</c> for
    /// processing to complete before returning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Long-polling enables synchronous-style request/response patterns over the durable inbox/outbox.
    /// When enabled, senders can wait for responses without implementing explicit callback handlers.
    /// </para>
    /// <para>
    /// Disabling long-polling reduces memory overhead (no TaskCompletionSource tracking) but requires
    /// senders to use separate response handlers.
    /// </para>
    /// <para>
    /// Even when disabled, the <c>DeliveryOptions.PollTimeout</c> parameter is accepted but ignored.
    /// </para>
    /// </remarks>
    /// <value>
    /// <see langword="true"/> to enable long-polling support; <see langword="false"/> to disable.
    /// Defaults to <see langword="true"/>.
    /// </value>
    public bool EnableLongPolling { get; set; } = true;

    /// <summary>
    /// Gets or sets the default timeout for long-polling when <c>DeliveryOptions.PollTimeout</c> is not specified.
    /// This value is used only if <see cref="EnableLongPolling"/> is <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A shorter timeout (e.g., 5 seconds) reduces the time senders wait for responses, improving
    /// responsiveness for failed or slow handlers. A longer timeout (e.g., 60 seconds) reduces
    /// the need for explicit polling but increases resource usage for long-running operations.
    /// </para>
    /// <para>
    /// If the handler completes before the timeout, <c>DeliverAsync</c> returns <c>DeliveryResult.Processed()</c>
    /// immediately. If the timeout expires, <c>DeliverAsync</c> returns <c>DeliveryResult.Pending()</c>.
    /// Timeout and caller cancellation affect only the poll. Durable handler execution continues until completion
    /// or activation shutdown.
    /// </para>
    /// </remarks>
    /// <value>
    /// The default long-polling timeout. Must be greater than zero. Defaults to 30 seconds.
    /// </value>
    public TimeSpan DefaultPollTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the base delay between retry attempts when outbox delivery encounters backpressure.
    /// The actual delay includes jitter (up to 50% additional random delay) to prevent thundering herd effects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the target inbox is at capacity and returns <c>DeliveryResult.Backpressured()</c>,
    /// the outbox delivery pump will wait this duration (plus jitter) before retrying.
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
    /// or if <see cref="DefaultPollTimeout"/> is less than or equal to <see cref="TimeSpan.Zero"/>.
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

        if (DefaultPollTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultPollTimeout), DefaultPollTimeout, "DefaultPollTimeout must be greater than TimeSpan.Zero.");
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

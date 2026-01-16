using System;

namespace Orleans.Journaling.Configuration;

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
    /// This controls parallelism for handler invocation within a single grain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A value of 1 enforces sequential processing, which simplifies handler logic but may
    /// reduce throughput. Higher values (e.g., 4-8) allow concurrent handler execution,
    /// improving throughput for I/O-bound handlers.
    /// </para>
    /// <para>
    /// Note: Handlers must be thread-safe if <c>ProcessingConcurrency &gt; 1</c>. Handler exceptions
    /// are caught and logged individually, so one failing handler does not block others.
    /// </para>
    /// <para>
    /// Concurrency affects only handler invocation. Message delivery and persistence are
    /// always serialized to maintain ordering guarantees during state machine transitions.
    /// </para>
    /// </remarks>
    /// <value>
    /// The maximum concurrent handler invocations. Must be greater than zero. Defaults to 1 (sequential processing).
    /// </value>
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
    /// senders to use observer patterns (<c>IDurableInboxObserver</c>) or separate response handlers.
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
    /// </para>
    /// </remarks>
    /// <value>
    /// The default long-polling timeout. Must be greater than zero. Defaults to 30 seconds.
    /// </value>
    public TimeSpan DefaultPollTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Validates the configuration values and throws if any are invalid.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <see cref="MaxCapacity"/> is less than or equal to zero,
    /// or if <see cref="ProcessingConcurrency"/> is less than or equal to zero,
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

        if (ProcessingConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ProcessingConcurrency), ProcessingConcurrency, "ProcessingConcurrency must be greater than zero.");
        }

        if (DefaultPollTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultPollTimeout), DefaultPollTimeout, "DefaultPollTimeout must be greater than TimeSpan.Zero.");
        }
    }
}

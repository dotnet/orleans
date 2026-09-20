using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableMessaging;

/// <summary>
/// Prepares and stages durable messages for delivery.
/// </summary>
/// <remarks>
/// <para>
/// The outbox stores pending outbound messages in a durable dictionary until they are successfully delivered
/// to the target grain's inbox. Messages persist atomically with grain state via <c>IJournaledStateManager.WriteStateAsync()</c>.
/// </para>
/// <para>
/// Delivery is driven by the outbox's background pump, which iterates
/// pending messages and calls <c>IDurableInboxExtension.DeliverAsync()</c> on target grains. Messages are
/// removed from the outbox when <see cref="DeliveryResult.Status"/> is
/// <see cref="DeliveryStatus.Accepted"/>, <see cref="DeliveryStatus.Duplicate"/>, or
/// <see cref="DeliveryStatus.DeadLettered"/>.
/// </para>
/// <para>
/// Messages are independently dispatched from dictionary storage. Applications establish ordering
/// with sequence numbers or correlation keys when needed.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Prepare a batch during handler preparation.
/// var envelope = context.CreateEnvelope()
///     .To(targetGrain, "payment/process")
///     .WithBody(new PaymentRequest { Amount = 100.00m })
///     .WithCorrelationKey("order-12345")
///     .WithReplyTo(context.GrainId)
///     .Build();
///
/// var batch = await context.Outbox.PrepareSendAsync([envelope], ct);
/// return () => context.Send(batch);
/// </code>
/// </example>
public interface IDurableOutbox
{
    /// <summary>
    /// Number of pending outbound messages.
    /// </summary>
    /// <remarks>
    /// Used for monitoring and backpressure signaling. A high count may indicate delivery issues
    /// or backpressure from target grains.
    /// </remarks>
    int Count { get; }

    /// <summary>
    /// Gets all pending outbound messages (no ordering guarantee).
    /// </summary>
    /// <remarks>
    /// Used by the delivery pump to iterate and deliver pending messages. The order of enumeration
    /// is undefined and may change between calls.
    /// </remarks>
    IEnumerable<DurableEnvelope> Messages { get; }

    /// <summary>
    /// Prepares outgoing messages and confirms a viable durable self-wakeup before staging.
    /// </summary>
    /// <param name="messages">The fully built envelopes to prepare.</param>
    /// <param name="cancellationToken">The token used to cancel the caller's wait.</param>
    /// <returns>An activation-local batch which can be synchronously staged using <see cref="Send"/>.</returns>
    /// <remarks>
    /// <para>
    /// Preparation copies the input collection and validates the envelopes before its first asynchronous wait.
    /// Complete this operation before applying shared business or journaled mutations. An empty collection
    /// produces a valid no-op batch and requires no new wakeup. Duplicate and orphan wakeups are resolved
    /// using the grain's durable state; a wakeup arriving during unresolved preparation or persistence defers.
    /// </para>
    /// <para>
    /// Once preparation is owned, cancellation ends only the caller's wait. Scheduling retains its resources
    /// through the actual outcome. If that wait is canceled, the implementation releases its unclaimed batch
    /// after scheduling completes. Activation retirement cleans up remaining owned preparation.
    /// </para>
    /// <para>
    /// Inbox handlers acquire batches through <see cref="IInboxHandlerContext.Outbox"/> during
    /// <see cref="IInboxHandler.PrepareAsync"/>. The runtime tracks preparations from their start and owns
    /// resulting batches through attempt completion, including late results after cancellation or failure.
    /// Retain a batch for the returned action. Ordinary callers must await every preparation operation and
    /// dispose each successfully returned batch, keeping its scope through staging and the journal write.
    /// </para>
    /// <para>
    /// Handler preparation consumes each returned completion and handles or propagates failures before
    /// returning its action. Retrieving a failed result counts as consumption even when retrieval throws;
    /// status inspection leaves the result unconsumed. The runtime rejects unfinished acquisitions and
    /// failed or canceled completions which remain unconsumed.
    /// </para>
    /// <para>
    /// Task conversion, such as <c>AsTask()</c>, retrieves the <see cref="ValueTask{TResult}"/> result on behalf
    /// of the resulting task. That task belongs to the caller, which awaits or handles its outcome before
    /// returning the action. Runtime consumption tracking applies to the returned value task; handling
    /// the converted task's outcome remains the caller's responsibility.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The envelopes conflict with pending message identities or the preparation scope is invalid.</exception>
    /// <exception cref="OperationCanceledException">The caller's wait was canceled.</exception>
    ValueTask<IPreparedOutboxBatch> PrepareSendAsync(IReadOnlyList<DurableEnvelope> messages, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously stages a prepared batch alongside safe-to-commit business mutations.
    /// </summary>
    /// <param name="batch">The live batch prepared by this outbox.</param>
    /// <remarks>
    /// <para>
    /// After preparation and revalidation, apply business mutations and call this method in one synchronous
    /// block, then request ordinary journal persistence. Staging transfers wakeup and intent ownership to
    /// the outbox's pending, captured, and acknowledged cohorts. Disposing a staged batch preserves that
    /// ownership through the actual persistence outcome. External dispatch begins after acknowledgement
    /// of the captured intents; messages remain pending until durable inbox acceptance.
    /// </para>
    /// <para>
    /// Repeatedly sending the same live, already-staged batch has no additional effect within a valid
    /// current scope. Every call requires the batch's owning activation and scope. Handler calls require
    /// the matching returned action, via its context or outbox. Outside-scope, stale, wrong-attempt,
    /// disposed, or foreign handles are rejected before any mutation.
    /// </para>
    /// <para>
    /// Existing equivalent envelopes with the same message ID are preserved. Conflicting identities are
    /// rejected. Equivalence includes routing and correlation fields, creation time, declared body type,
    /// body bytes, and request-context bytes and type metadata.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Ordinary application code keeps ownership through the journal write.
    /// using var batch = await outbox.PrepareSendAsync([envelope], ct);
    /// ApplyPreparedBusinessChanges();
    /// outbox.Send(batch);
    /// await stateManager.WriteStateAsync(ct);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="batch"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The batch has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The batch belongs to another owner or attempt, its scope is invalid, or an envelope conflicts with an existing message.</exception>
    void Send(IPreparedOutboxBatch batch);

    /// <summary>
    /// Tries to get a specific outbox message.
    /// </summary>
    /// <param name="messageId">The unique identifier of the message.</param>
    /// <param name="envelope">When this method returns, contains the envelope if found; otherwise, the default value.</param>
    /// <returns><c>true</c> if the message was found; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// Used for diagnostics, monitoring, or manual retry operations. In normal operation, the delivery pump
    /// iterates messages via the <see cref="Messages"/> property.
    /// </remarks>
    bool TryGetMessage(Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope);

}

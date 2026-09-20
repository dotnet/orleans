using System;

namespace Orleans.DurableMessaging;

/// <summary>
/// An opaque, activation-local capability for synchronously staging prepared outgoing messages.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IDurableOutbox.PrepareSendAsync"/> creates a batch bound to its owning outbox and activation.
/// Retain the original envelopes for message metadata. Use the batch with <see cref="IDurableOutbox.Send"/>
/// after preparation completes and before its owning scope ends.
/// </para>
/// <para>
/// <see cref="IDisposable.Dispose"/> abandons an unstaged batch's local preparation ownership, allowing
/// an orphan wakeup to retire once the owned scheduling outcome is known. After staging, disposal leaves
/// wakeup and intent ownership with the outbox through the actual journal acknowledgement or failure.
/// Repeated disposal is safe.
/// </para>
/// <para>
/// The inbox runtime disposes batches acquired through the handler's outbox when that attempt ends.
/// Ordinary callers dispose their batches after the staging and persistence scope.
/// </para>
/// </remarks>
public interface IPreparedOutboxBatch : IDisposable
{
}

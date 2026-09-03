using Orleans.Runtime;

namespace Orleans.DurableMessaging;

internal sealed class InboxHandlerSelectionContext(
    DurableEnvelope envelope,
    GrainId grainId) : IInboxHandlerContext
{
    public DurableEnvelope Envelope { get; } = envelope;

    public GrainId GrainId { get; } = grainId;

    public IDurableOutbox Outbox =>
        throw new InvalidOperationException("Handler selection is read-only and cannot access the durable outbox.");

    public DurableEnvelopeBuilder CreateEnvelope() =>
        throw new InvalidOperationException("Handler selection is read-only and cannot create outbound envelopes.");

    public void Send(DurableEnvelope envelope) =>
        throw new InvalidOperationException("Handler selection is read-only and cannot send outbound messages.");
}

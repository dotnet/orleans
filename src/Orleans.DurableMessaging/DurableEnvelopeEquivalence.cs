using System;

namespace Orleans.DurableMessaging;

internal static class DurableEnvelopeEquivalence
{
    public static bool AreEquivalent(DurableEnvelope left, DurableEnvelope right) =>
        left.MessageId == right.MessageId
        && left.SenderId == right.SenderId
        && left.ReceiverId == right.ReceiverId
        && (ReferenceEquals(left.Payload, right.Payload)
            || left.Payload is not null && right.Payload is not null
                && left.Payload.Memory.Span.SequenceEqual(right.Payload.Memory.Span));
}

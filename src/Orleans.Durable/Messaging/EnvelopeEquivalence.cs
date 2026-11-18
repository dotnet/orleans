using System;

namespace Orleans.Messaging;

internal static class EnvelopeEquivalence
{
    public static bool AreEquivalent(OutboxMessage left, OutboxMessage right) =>
        left.ReceiverId == right.ReceiverId && AreSameCommand(left.Envelope, right.Envelope);

    public static bool AreSameCommand(InboxMessage left, InboxMessage right) => AreSameCommand(left.Envelope, right.Envelope);

    public static bool AreSameCommand(Envelope left, Envelope right) =>
        left.MessageId == right.MessageId
        && left.Payload.Length == right.Payload.Length
        && string.Equals(left.Subject, right.Subject, StringComparison.Ordinal)
        && left.DeliverAt == right.DeliverAt
        && left.Payload.AsSpan().SequenceEqual(right.Payload);
}

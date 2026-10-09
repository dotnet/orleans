using System;
using System.Buffers;

namespace Orleans.DurableMessaging;

internal static class DurableEnvelopeEquivalence
{
    public static bool AreEquivalent(DurableEnvelope left, DurableEnvelope right) =>
        left.SenderId == right.SenderId && AreSameCommand(left, right);

    public static bool AreSameCommand(DurableEnvelope left, DurableEnvelope right)
    {
        if (left.MessageId != right.MessageId || left.ReceiverId != right.ReceiverId
            || !string.Equals(left.Subject, right.Subject, StringComparison.Ordinal)
            || left.Payload.Length != right.Payload.Length)
        {
            return false;
        }
        var first = new SequenceReader<byte>(left.Payload.AsReadOnlySequence());
        var second = new SequenceReader<byte>(right.Payload.AsReadOnlySequence());
        while (first.TryRead(out var value))
        {
            if (!second.TryRead(out var other) || value != other) return false;
        }
        return true;
    }
}

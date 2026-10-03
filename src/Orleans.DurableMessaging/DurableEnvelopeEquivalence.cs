using System;
using System.Buffers;
using System.Linq;

namespace Orleans.DurableMessaging;

internal static class DurableEnvelopeEquivalence
{
    public static bool AreEquivalent(DurableEnvelope left, DurableEnvelope right)
    {
        if (left.MessageId != right.MessageId
            || left.SenderId != right.SenderId
            || left.ReceiverId != right.ReceiverId
            || !string.Equals(left.RouteKey, right.RouteKey, StringComparison.Ordinal)
            || !Equals(left.CorrelationKey, right.CorrelationKey)
            || !Nullable.Equals(left.ReplyTo, right.ReplyTo)
            || left.CreatedAt != right.CreatedAt)
        {
            return false;
        }

        if (ReferenceEquals(left.Data, right.Data))
        {
            return true;
        }

        if (left.Data is null || right.Data is null
            || !left.Data.HasEquivalentDeclaredTypes(right.Data)
            || !SequenceEqual(left.Data.GetBodyBytes(), right.Data.GetBodyBytes()))
        {
            return false;
        }

        var leftContextKeys = left.Data.ContextKeys.ToHashSet(StringComparer.Ordinal);
        var rightContextKeys = right.Data.ContextKeys.ToHashSet(StringComparer.Ordinal);
        if (!leftContextKeys.SetEquals(rightContextKeys))
        {
            return false;
        }

        foreach (var key in leftContextKeys)
        {
            if (!left.Data.TryGetContextBytes(key, out var leftContext)
                || !right.Data.TryGetContextBytes(key, out var rightContext)
                || !SequenceEqual(leftContext, rightContext))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SequenceEqual(ReadOnlySequence<byte> left, ReadOnlySequence<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        return left.IsSingleSegment && right.IsSingleSegment
            ? left.FirstSpan.SequenceEqual(right.FirstSpan)
            : left.ToArray().AsSpan().SequenceEqual(right.ToArray());
    }
}

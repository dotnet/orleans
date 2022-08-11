using System;

namespace Orleans.Streams
{
    /// <summary>
    /// Mark a subscriptionId as either an implicit subscription Id, or an explicit subscription Id.
    /// high bit of last byte in guild is the subscription type flag.
    /// 1: implicit subscription
    /// 0: explicit subscription
    /// </summary>
    internal static class SubscriptionMarker
    {
        internal static Guid MarkAsExplicitSubscriptionId(Guid subscriptionGuid)
        {
            return MarkSubscriptionGuid(subscriptionGuid, false);
        }

        internal static Guid MarkAsImplictSubscriptionId(Guid subscriptionGuid)
        {
            return MarkSubscriptionGuid(subscriptionGuid, true);
        }

        internal static bool IsImplicitSubscription(Guid subscriptionGuid)
        {
            Span<byte> guidBytes = stackalloc byte[16];
            subscriptionGuid.TryWriteBytes(guidBytes);
            // return true if high bit of last byte is set
            return (guidBytes[15] & 0x80) != 0;
        }

        private static Guid MarkSubscriptionGuid(Guid subscriptionGuid, bool isImplicitSubscription)
        {
            Span<byte> guidBytes = stackalloc byte[16];
            subscriptionGuid.TryWriteBytes(guidBytes);
            if (isImplicitSubscription)
            {
                // set high bit of last byte
                guidBytes[15] |= 0x80;
            }
            else
            {
                // clear high bit of last byte
                guidBytes[15] &= 0x7f;
            }

            return new Guid(guidBytes);
        }
    }
}

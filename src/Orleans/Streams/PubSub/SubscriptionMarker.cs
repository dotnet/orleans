using System;

namespace Orleans.Streams
{
    /// <summary>
    /// Mark a subscriptionId as either an implicit subscription Id, or a explicit subscription Id.
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
            byte[] guidBytes = subscriptionGuid.ToByteArray();
            // return true if high bit of last byte is set
            return guidBytes[guidBytes.Length - 1] == (byte)(guidBytes[guidBytes.Length - 1] | 0x80);
        }

        private static Guid MarkSubscriptionGuid(Guid subscriptionGuid, bool isImplicitSubscription)
        {
            byte[] guidBytes = subscriptionGuid.ToByteArray();
            if (isImplicitSubscription)
            {
                // set high bit of last byte
                guidBytes[guidBytes.Length - 1] = (byte)(guidBytes[guidBytes.Length - 1] | 0x80);
            }
            else
            {
                // clear high bit of last byte
                guidBytes[guidBytes.Length - 1] = (byte)(guidBytes[guidBytes.Length - 1] & 0x7f);
            }

            return new Guid(guidBytes);
        }
    }
}

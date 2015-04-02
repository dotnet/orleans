/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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

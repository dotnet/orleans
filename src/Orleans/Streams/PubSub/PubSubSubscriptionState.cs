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
using Orleans.Runtime;

namespace Orleans.Streams
{
    [Serializable]
    internal class PubSubSubscriptionState : IEquatable<PubSubSubscriptionState>
    {
        // These fields have to be public non-readonly for JSonSerialization to work!
        // Implement ISerializable if changing any of them to readonly
        public GuidId SubscriptionId;
        public StreamId Stream;
        public IStreamConsumerExtension Consumer;
        public StreamSequenceToken StreamSequenceToken;
        public IStreamFilterPredicateWrapper FilterWrapper; // Serialized func info

        // This constructor has to be public for JSonSerialization to work!
        // Implement ISerializable if changing it to non-public
        public PubSubSubscriptionState(
            GuidId subscriptionId,
            StreamId streamId,
            IStreamConsumerExtension streamConsumer,
            StreamSequenceToken token,
            IStreamFilterPredicateWrapper filterWrapper)
        {
            SubscriptionId = subscriptionId;
            Stream = streamId;
            Consumer = streamConsumer;
            StreamSequenceToken = token;
            FilterWrapper = filterWrapper;
        }

        public IStreamFilterPredicateWrapper Filter { get { return FilterWrapper; } }

        internal void AddFilter(IStreamFilterPredicateWrapper newFilter)
        {
            if (FilterWrapper == null)
            {
                // No existing filter - add single
                FilterWrapper = newFilter;
            }
            else if (FilterWrapper is OrFilter)
            {
                // Existing multi-filter - add new filter to it
                ((OrFilter) FilterWrapper).AddFilter(newFilter);
            }
            else
            {
                // Exsiting single filter - convert to multi-filter
                FilterWrapper = new OrFilter(FilterWrapper, newFilter);
            }
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            // Note: Can't use the 'as' operator on PubSubSubscriptionState because it is a struct.
            return obj is PubSubSubscriptionState && Equals((PubSubSubscriptionState) obj);
        }
        public bool Equals(PubSubSubscriptionState other)
        {
            // Note: PubSubSubscriptionState is a struct, so 'other' can never be null.
            return Equals(other.SubscriptionId);
        }
        public bool Equals(GuidId subscriptionId)
        {
            if (ReferenceEquals(null, subscriptionId)) return false;
            return SubscriptionId.Equals(subscriptionId);
        }

        public override int GetHashCode()
        {
            return SubscriptionId.GetHashCode();
        }

        public static bool operator ==(PubSubSubscriptionState left, PubSubSubscriptionState right)
        {
            if ((object)left == null && (object)right == null)
                return true;
            if ((object)left != null)
            {
                return left.Equals(right);
            }
            return false;
        }

        public static bool operator !=(PubSubSubscriptionState left, PubSubSubscriptionState right)
        {
            return !(left == right);
        }

        public override string ToString()
        {
            return string.Format("PubSubSubscriptionState:SubscriptionId={0},StreamId={1},Consumer={2},SequenceToken={3}.",
                SubscriptionId, Stream, Consumer, StreamSequenceToken);
        }
    }
}

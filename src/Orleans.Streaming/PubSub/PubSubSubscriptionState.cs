using System;
using Newtonsoft.Json;
using Orleans.Runtime;

namespace Orleans.Streams
{
    [Serializable]
    [JsonObject(MemberSerialization.OptIn)]
    [GenerateSerializer]
    public sealed class PubSubSubscriptionState : IEquatable<PubSubSubscriptionState>
    {
        public enum SubscriptionStates
        {
            Active,
            Faulted,
        }

        // IMPORTANT!!!!!
        // These fields have to be public non-readonly for JSonSerialization to work!
        // Implement ISerializable if changing any of them to readonly
        [JsonProperty]
        [Id(0)]
        public GuidId SubscriptionId;

        [JsonProperty]
        [Id(1)]
        public QualifiedStreamId Stream;

        [JsonProperty]
        [Id(2)]
        public GrainId Consumer; // the field needs to be of a public type, otherwise we will not generate an Orleans serializer for that class.

        [JsonProperty]
        [Id(3)]
        public string FilterData; // Serialized func info

        [JsonProperty]
        [Id(4)]
        public SubscriptionStates state;

        [JsonIgnore]
        public bool IsFaulted { get { return state == SubscriptionStates.Faulted; } }

        // This constructor has to be public for JSonSerialization to work!
        // Implement ISerializable if changing it to non-public
        public PubSubSubscriptionState(
            GuidId subscriptionId,
            QualifiedStreamId streamId,
            GrainId streamConsumer)
        {
            SubscriptionId = subscriptionId;
            Stream = streamId;
            Consumer = streamConsumer;
            state = SubscriptionStates.Active;
        }

        public void AddFilter(string filterData)
        {
            this.FilterData = filterData;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            // Note: Can't use the 'as' operator on PubSubSubscriptionState because it is a struct.
            return obj is PubSubSubscriptionState && Equals((PubSubSubscriptionState) obj);
        }

        public bool Equals(PubSubSubscriptionState other)
        {
            if ((object)other == null)
                return false;
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
            return string.Format("PubSubSubscriptionState:SubscriptionId={0},StreamId={1},Consumer={2}.",
                SubscriptionId, Stream, Consumer);
        }

        public void Fault()
        {
            state = SubscriptionStates.Faulted;
        }
    }
}

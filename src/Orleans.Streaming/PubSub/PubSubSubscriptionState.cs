using System;
using Newtonsoft.Json;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Represents the pub/sub registration state for a stream subscription.
    /// </summary>
    [Serializable]
    [JsonObject(MemberSerialization.OptIn)]
    [GenerateSerializer]
    public sealed class PubSubSubscriptionState : IEquatable<PubSubSubscriptionState?>, System.Text.Json.Serialization.IJsonOnDeserialized
    {
        /// <summary>
        /// Identifies the lifecycle state of a stream subscription.
        /// </summary>
        public enum SubscriptionStates
        {
            /// <summary>
            /// The subscription is active.
            /// </summary>
            Active,

            /// <summary>
            /// The subscription is faulted.
            /// </summary>
            Faulted,
        }

        // IMPORTANT!!!!!
        // These fields have to be public non-readonly for JSonSerialization to work!
        // Implement ISerializable if changing any of them to readonly
        /// <summary>
        /// The subscription identifier.
        /// </summary>
        [JsonProperty]
        [System.Text.Json.Serialization.JsonInclude]
        [System.Text.Json.Serialization.JsonPropertyName("subscriptionId")]
        [Id(0)]
        public GuidId SubscriptionId;

        /// <summary>
        /// The qualified identifier of the subscribed stream.
        /// </summary>
        [JsonProperty]
        [System.Text.Json.Serialization.JsonInclude]
        [System.Text.Json.Serialization.JsonPropertyName("stream")]
        [Id(1)]
        public QualifiedStreamId Stream;

        /// <summary>
        /// The identifier of the grain which consumes the stream.
        /// </summary>
        [JsonProperty]
        [System.Text.Json.Serialization.JsonInclude]
        [System.Text.Json.Serialization.JsonPropertyName("consumer")]
        [Id(2)]
        public GrainId Consumer; // the field needs to be of a public type, otherwise we will not generate an Orleans serializer for that class.

        /// <summary>
        /// The serialized filter data associated with the subscription.
        /// </summary>
        [JsonProperty]
        [System.Text.Json.Serialization.JsonInclude]
        [System.Text.Json.Serialization.JsonPropertyName("filterData")]
        [Id(3)]
        public string? FilterData; // Serialized func info

        /// <summary>
        /// The lifecycle state of the subscription.
        /// </summary>
        [JsonProperty]
        [System.Text.Json.Serialization.JsonInclude]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        [System.Text.Json.Serialization.JsonPropertyName("state")]
        [Id(4)]
        public SubscriptionStates state;

        /// <summary>
        /// Gets a value indicating whether the subscription is faulted.
        /// </summary>
        [JsonIgnore]
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsFaulted { get { return state == SubscriptionStates.Faulted; } }

        // This constructor has to be public for JSonSerialization to work!
        // Implement ISerializable if changing it to non-public
        /// <summary>
        /// Initializes an active stream subscription registration.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="streamId">The qualified stream identifier.</param>
        /// <param name="streamConsumer">The identifier of the grain which consumes the stream.</param>
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

        /// <summary>
        /// Initializes a stream subscription registration.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="stream">The qualified stream identifier.</param>
        /// <param name="consumer">The identifier of the grain which consumes the stream.</param>
        /// <param name="filterData">The serialized filter data associated with the subscription.</param>
        /// <param name="state">The lifecycle state of the subscription.</param>
        [JsonConstructor]
        [System.Text.Json.Serialization.JsonConstructor]
        public PubSubSubscriptionState(
            GuidId subscriptionId,
            QualifiedStreamId stream,
            GrainId consumer,
            string? filterData,
            SubscriptionStates state)
            : this(subscriptionId, stream, consumer)
        {
            FilterData = filterData;
            this.state = state;
        }

        /// <summary>
        /// Sets the serialized filter data associated with the subscription.
        /// </summary>
        /// <param name="filterData">The serialized filter data.</param>
        public void AddFilter(string? filterData)
        {
            this.FilterData = filterData;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            // Note: Can't use the 'as' operator on PubSubSubscriptionState because it is a struct.
            return obj is PubSubSubscriptionState && Equals((PubSubSubscriptionState) obj);
        }

        /// <inheritdoc/>
        public bool Equals(PubSubSubscriptionState? other)
        {
            if ((object?)other == null)
                return false;
            // Note: PubSubSubscriptionState is a struct, so 'other' can never be null.
            return Equals(other.SubscriptionId);
        }

        /// <summary>
        /// Determines whether this subscription has the specified identifier.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier to compare.</param>
        /// <returns><see langword="true"/> if the identifiers are equal; otherwise, <see langword="false"/>.</returns>
        public bool Equals(GuidId? subscriptionId)
        {
            if (ReferenceEquals(null, subscriptionId)) return false;
            return SubscriptionId.Equals(subscriptionId);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            return SubscriptionId.GetHashCode();
        }

        /// <summary>
        /// Compares two subscription states for equality by subscription identifier.
        /// </summary>
        /// <param name="left">The first subscription state.</param>
        /// <param name="right">The second subscription state.</param>
        /// <returns>
        /// <see langword="true"/> if both values are <see langword="null"/> or have the same subscription identifier;
        /// otherwise, <see langword="false"/>.
        /// </returns>
        public static bool operator ==(PubSubSubscriptionState? left, PubSubSubscriptionState? right)
        {
            if ((object?)left == null && (object?)right == null)
                return true;
            if ((object?)left != null)
            {
                return left.Equals(right);
            }
            return false;
        }

        /// <summary>
        /// Compares two subscription states for inequality by subscription identifier.
        /// </summary>
        /// <param name="left">The first subscription state.</param>
        /// <param name="right">The second subscription state.</param>
        /// <returns><see langword="true"/> if the values are not equal; otherwise, <see langword="false"/>.</returns>
        public static bool operator !=(PubSubSubscriptionState? left, PubSubSubscriptionState? right)
        {
            return !(left == right);
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return string.Format("PubSubSubscriptionState:SubscriptionId={0},StreamId={1},Consumer={2}.",
                SubscriptionId, Stream, Consumer);
        }

        /// <summary>
        /// Marks the subscription as faulted.
        /// </summary>
        public void Fault()
        {
            state = SubscriptionStates.Faulted;
        }

        void System.Text.Json.Serialization.IJsonOnDeserialized.OnDeserialized()
        {
            if (SubscriptionId is null
                || string.IsNullOrWhiteSpace(Stream.ProviderName)
                || Stream.StreamId == default
                || Consumer.IsDefault
                || !Enum.IsDefined(state))
            {
                throw new System.Text.Json.JsonException($"Could not deserialize {nameof(PubSubSubscriptionState)}.");
            }
        }
    }
}

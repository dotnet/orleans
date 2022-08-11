using Orleans.Runtime;
using System;
using System.Collections.Generic;


namespace Orleans.Storage
{
    /// <summary>
    /// Struct contains choice on streaming, storage deserializer and storage serializer
    /// </summary>
    public struct SerializationChoice
    {
        /// <summary>
        /// <em>TRUE</em> if streaming is preferred.
        /// </summary>
        public bool PreferStreaming { get; }

        /// <summary>
        /// The <see cref="IStorageDeserializer"/> that should be used.
        /// </summary>
        public IStorageDeserializer Deserializer { get; }

        /// <summary>
        /// /// The <see cref="IStorageSerializer"/> that should be used.
        /// </summary>
        public IStorageSerializer Serializer { get; }


        /// <summary>
        /// Implements "a tuple" that encodes the desired (de)serialization choice and streaming preference.
        /// </summary>
        /// <param name="preferStreaming">Is streaming to be preferred.</param>
        /// <param name="deserializer">The <see cref="IStorageDeserializer"/> used.</param>
        /// <param name="serializer"><see cref="IStorageSerializer"/> used.</param>
        /// <remarks>Note that only one, either <paramref name="deserializer"/> or <paramref name="serializer"/> can be defined, not both.</remarks>
        public SerializationChoice(bool preferStreaming, IStorageDeserializer deserializer, IStorageSerializer serializer)
        {
            if(deserializer == null && serializer == null)
            {
                throw new ArgumentException($"Either {nameof(deserializer)} or {nameof(serializer)} needs to be defined (not both).");
            }

            if(deserializer != null && serializer != null)
            {
                throw new ArgumentException($"Either {nameof(deserializer)} or {nameof(serializer)} needs to be defined (not both).");
            }

            PreferStreaming = preferStreaming;
            Deserializer = deserializer;
            Serializer = serializer;
        }
    }

    /// <summary>
    /// A strategy to pick a serializer or a deserializer for storage operations. As for an example, this can be used to:
    /// 1) Add a custom serializer or deserializer for use in storage provider operations (e.g. ProtoBuf or something else).
    /// 2) In combination with serializer or deserializer to update stored object version.
    /// 3) Per-grain storage format selection
    /// 4) Switch storage format first by reading using the save format and then writing in the new format.
    /// </summary>
    public interface IStorageSerializationPicker
    {
        /// <summary>
        /// The configured deserializers.
        /// </summary>
        ICollection<IStorageDeserializer> Deserializers { get; }

        /// <summary>
        /// The configured serializers.
        /// </summary>
        ICollection<IStorageSerializer> Serializers { get; }

        /// <summary>Picks a deserializer using the given parameters.</summary>
        /// <param name="serviceId">The ID of the current service.</param>
        /// <param name="storageProviderInstanceName">The requesting storage provider.</param>
        /// <param name="grainType">The type of grain.</param>
        /// <param name="grainId">The grain ID.</param>
        /// <param name="grainState">The grain state.</param>
        /// <param name="tag">An optional tag parameter that might be used by the storage parameter for "out-of-band" contracts.</param>
        /// <returns>A serializer or <em>null</em> if not match was found.</returns>
        SerializationChoice PickDeserializer<T>(string serviceId, string storageProviderInstanceName, string grainType, GrainId grainId, IGrainState<T> grainState, string tag = null);

        /// <summary>Picks a serializer using the given parameters.</summary>
        /// <param name="serviceId">The ID of the current service.</param>
        /// <param name="storageProviderInstanceName">The requesting storage provider.</param>
        /// <param name="grainType">The type of grain.</param>
        /// <param name="grainId">The grain ID.</param>
        /// <param name="grainState">The grain state.</param>
        /// <param name="tag">An optional tag parameter that might be used by the storage parameter for "out-of-band" contracts.</param>
        /// <returns>A deserializer or <em>null</em> if not match was found.</returns>
        SerializationChoice PickSerializer<T>(string serviceId, string storageProviderInstanceName, string grainType, GrainId grainId, IGrainState<T> grainState, string tag = null);
    }
}

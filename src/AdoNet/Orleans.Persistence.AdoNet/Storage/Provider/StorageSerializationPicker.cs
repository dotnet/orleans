using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;


namespace Orleans.Storage
{
    /// <summary>
    /// A strategy to pick a serializer or a deserializer for storage operations. This can be used to:
    /// 1) Add a custom serializer or deserializer for use in storage provider operations.
    /// 2) In combination with serializer or deserializer to update stored object version.
    /// 3) Per-grain storage format selection
    /// 4) Switch storage format first by reading using the save format and then writing in the new format.
    /// </summary>
    public class DefaultRelationalStoragePicker: IStorageSerializationPicker
    {
        /// <summary>
        /// The configured deserializers.
        /// </summary>
        public ICollection<IStorageDeserializer> Deserializers { get; }

        /// <summary>
        /// The configured serializers.
        /// </summary>
        public ICollection<IStorageSerializer> Serializers { get; }


        /// <summary>
        /// Constructs the serializers from the given configuration properties.
        /// </summary>
        /// <param name="deserializers">The deserializers to be used.</param>
        /// <param name="serializers">The serializers to be used.</param>
        public DefaultRelationalStoragePicker(IEnumerable<IStorageDeserializer> deserializers, IEnumerable<IStorageSerializer> serializers)
        {
            if(deserializers == null)
            {
                throw new ArgumentNullException(nameof(deserializers));
            }

            if(serializers == null)
            {
                throw new ArgumentNullException(nameof(serializers));
            }

            Deserializers = new Collection<IStorageDeserializer>(new List<IStorageDeserializer>(deserializers));
            Serializers = new Collection<IStorageSerializer>(new List<IStorageSerializer>(serializers));
        }


        /// <summary>
        /// Picks a deserializer using the given parameters.
        /// <see cref="IStorageSerializationPicker.PickDeserializer"/>
        /// </summary>
        public SerializationChoice PickDeserializer<T>(string serviceId, string storageProviderInstanceName, string grainType, GrainReference grainReference, IGrainState<T> grainState, string tag = null)
        {
            //If the tag has been given, try to pick that one and if not found, take the first on the list. This arrangement allows one to switch storage format more easily.
            var deserializer = Deserializers.FirstOrDefault(i => i.Tag == tag);
            return new SerializationChoice(false, deserializer ?? Deserializers.FirstOrDefault(), null);
        }


        /// <summary>
        /// Picks a serializer using the given parameters.
        /// <see cref="IStorageSerializationPicker.PickSerializer"/>
        /// </summary>
        public SerializationChoice PickSerializer<T>(string servideId, string storageProviderInstanceName, string grainType, GrainReference grainReference, IGrainState<T> grainState, string tag = null)
        {
            var serializer = Serializers.FirstOrDefault(i => i.Tag == tag);
            return new SerializationChoice(false, null, serializer ?? Serializers.FirstOrDefault());
        }
    }
}

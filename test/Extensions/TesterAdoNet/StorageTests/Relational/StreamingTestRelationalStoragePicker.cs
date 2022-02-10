using Orleans;
using Orleans.Runtime;
using Orleans.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace UnitTests.StorageTests.Relational.TestDataSets
{
    class StreamingTestRelationalStoragePicker: IStorageSerializationPicker
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
        public StreamingTestRelationalStoragePicker(IEnumerable<IStorageDeserializer> deserializers, IEnumerable<IStorageSerializer> serializers)
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
            return new SerializationChoice(true, deserializer ?? Deserializers.FirstOrDefault(), null);
        }


        /// <summary>
        /// Picks a serializer using the given parameters.
        /// <see cref="IStorageSerializationPicker.PickSerializer"/>
        /// </summary>
        public SerializationChoice PickSerializer<T>(string serviceId, string storageProviderInstanceName, string grainType, GrainReference grainReference, IGrainState<T> grainState, string tag = null)
        {
            var serializer = Serializers.FirstOrDefault(i => i.Tag == tag);
            return new SerializationChoice(true, null, serializer ?? Serializers.FirstOrDefault());
        }
    }
}

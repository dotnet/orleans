using Orleans.Serialization;
using System;
using System.Diagnostics;
using System.IO;


namespace Orleans.Storage
{
    /// <summary>
    /// A default binary deserializer for storage providers.
    /// </summary>
    [DebuggerDisplay("CanStream = {CanStream}, Tag = {Tag}")]
    public class OrleansStorageDefaultBinaryDeserializer: IStorageDeserializer
    {
        /// <summary>
        /// <see cref="IStorageDeserializer.CanStream"/>
        /// </summary>
        public bool CanStream { get; } = false;

        /// <summary>
        /// <see cref="IStorageDeserializer.Tag"/>
        /// </summary>
        public string Tag { get; }


        /// <summary>
        /// Constructs this deserializer from the given parameters.
        /// </summary>
        /// <param name="tag"><see cref="IStorageDeserializer.Tag"/>.</param>
        public OrleansStorageDefaultBinaryDeserializer(string tag)
        {
            if(string.IsNullOrWhiteSpace(tag))
            {
                throw new ArgumentException("The parameter should contain characters.", nameof(tag));
            }
            Tag = tag;
        }


        /// <summary>
        /// <see cref="IStorageDeserializer.Deserialize(Stream, Type)"/>.
        /// </summary>
        /// <exception cref="NotSupportedException"/>
        public object Deserialize(Stream dataStream, Type grainStateType)
        {
            throw new NotSupportedException($"{nameof(OrleansStorageDefaultBinaryDeserializer)} does not support stream deserialization.");
        }


        /// <summary>
        /// <see cref="IStorageDeserializer.Deserialize(object, Type)"/>.
        /// </summary>
        public object Deserialize(object data, Type grainStateType)
        {
            return SerializationManager.DeserializeFromByteArray<object>((byte[])data);
        }


        /// <summary>
        /// <see cref="IStorageDeserializer.Deserialize(TextReader, Type)"/>.
        /// </summary>
        public object Deserialize(TextReader reader, Type grainStateType)
        {
            throw new NotImplementedException();
        }
    }
}

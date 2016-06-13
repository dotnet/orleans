using Orleans.Serialization;
using System;
using System.Diagnostics;
using System.IO;


namespace Orleans.Storage
{
    /// <summary>
    /// A default binary serializer for storage providers.
    /// </summary>
    [DebuggerDisplay("CanStream = {CanStream}, Tag = {Tag}")]
    public class OrleansStorageDefaultBinarySerializer: IStorageSerializer
    {
        /// <summary>
        /// <see cref="IStorageSerializer.CanStream"/>
        /// </summary>
        public bool CanStream { get; } = false;

        /// <summary>
        /// <see cref="IStorageSerializer.Tag"/>
        /// </summary>
        public string Tag { get; }


        /// <summary>
        /// Constructs this serializer from the given parameters.
        /// </summary>
        /// <param name="tag"><see cref="IStorageSerializer.Tag"/>.</param>
        public OrleansStorageDefaultBinarySerializer(string tag)
        {
            if(string.IsNullOrWhiteSpace(tag))
            {
                throw new ArgumentException("The parameter should contain characters.", nameof(tag));
            }

            Tag = tag;
        }


        /// <summary>
        /// <see cref="IStorageSerializer.Serialize(Stream, object)"/>.
        /// </summary>
        /// <exception cref="NotSupportedException"/>
        public object Serialize(Stream stream, object data)
        {
            throw new NotSupportedException($"{nameof(OrleansStorageDefaultBinarySerializer)} does not support stream deserialization.");
        }


        /// <summary>
        /// <see cref="IStorageSerializer.Serialize(object)"/>.
        /// </summary>
        public object Serialize(object data)
        {
            return SerializationManager.SerializeToByteArray(data);
        }


        /// <summary>
        /// <see cref="IStorageSerializer.Serialize(TextWriter, object)"/>.
        /// </summary>
        public object Serialize(TextWriter writer, object data)
        {
            throw new NotImplementedException();
        }
    }
}

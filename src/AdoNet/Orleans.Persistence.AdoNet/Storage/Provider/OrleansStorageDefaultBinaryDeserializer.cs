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
        private readonly Serialization.Serializer serializer;

        /// <summary>
        /// <see cref="IStorageDeserializer.CanStream"/>
        /// </summary>
        public bool CanStream { get; } = true;

        /// <summary>
        /// <see cref="IStorageDeserializer.Tag"/>
        /// </summary>
        public string Tag { get; }


        /// <summary>
        /// Constructs this deserializer from the given parameters.
        /// </summary>
        /// <param name="serializer"></param>
        /// <param name="tag"><see cref="IStorageDeserializer.Tag"/>.</param>
        public OrleansStorageDefaultBinaryDeserializer(Serialization.Serializer serializer, string tag)
        {
            this.serializer = serializer;
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
            return this.serializer.Deserialize<object>(dataStream);
        }


        /// <summary>
        /// <see cref="IStorageDeserializer.Deserialize(object, Type)"/>.
        /// </summary>
        public object Deserialize(object data, Type grainStateType)
        {
            return this.serializer.Deserialize<object>((byte[])data);
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

using System;
using System.Diagnostics;
using System.IO;
using System.Xml;
using System.Xml.Serialization;


namespace Orleans.Storage
{
    /// <summary>
    /// A default XML deserializer for storage providers.
    /// </summary>
    [DebuggerDisplay("CanStream = {CanStream}, Tag = {Tag}")]
    public class OrleansStorageDefaultXmlDeserializer: IStorageDeserializer
    {
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
        /// <param name="tag"><see cref="IStorageDeserializer.Tag"/>.</param>
        public OrleansStorageDefaultXmlDeserializer(string tag)
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
        public object Deserialize(Stream dataStream, Type grainStateType)
        {
            var serializer = new XmlSerializer(grainStateType);
            return serializer.Deserialize(dataStream);
        }


        /// <summary>
        /// <see cref="IStorageDeserializer.Deserialize(object, Type)"/>.
        /// </summary>
        public object Deserialize(object data, Type grainStateType)
        {
            var deserializer = new XmlSerializer(grainStateType);
            using(var reader = new StringReader((string)data))
            {
                return deserializer.Deserialize(reader);
            }
        }


        /// <summary>
        /// <see cref="IStorageDeserializer.Deserialize(TextReader, Type)"/>.
        /// </summary>
        public object Deserialize(TextReader reader, Type grainStateType)
        {
            var serializer = new XmlSerializer(grainStateType);
            return serializer.Deserialize(reader);
        }
    }
}

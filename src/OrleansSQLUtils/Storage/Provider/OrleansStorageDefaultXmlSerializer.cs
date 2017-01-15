using System;
using System.Diagnostics;
using System.IO;
using System.Xml.Serialization;


namespace Orleans.Storage
{
    /// <summary>
    /// A default XML serializer for storage providers.
    /// </summary>
    [DebuggerDisplay("CanStream = {CanStream}, Tag = {Tag}")]
    public class OrleansStorageDefaultXmlSerializer: IStorageSerializer
    {
        /// <summary>
        /// <see cref="IStorageSerializer.CanStream"/>
        /// </summary>
        public bool CanStream { get; } = true;

        /// <summary>
        /// <see cref="IStorageSerializer.Tag"/>
        /// </summary>
        public string Tag { get; }


        /// <summary>
        /// Constructs this serializer from the given parameters.
        /// </summary>
        /// <param name="tag"><see cref="IStorageSerializer.Tag"/>.</param>
        public OrleansStorageDefaultXmlSerializer(string tag)
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
        public object Serialize(Stream dataStream, object data)
        {
            using(StreamWriter writer = new StreamWriter(dataStream))
            {
                var serializer = new XmlSerializer(data.GetType());
                serializer.Serialize(writer, data);
            }

            return dataStream;
        }


        /// <summary>
        /// <see cref="IStorageSerializer.Serialize(object)"/>.
        /// </summary>
        public object Serialize(object data)
        {
            using(StringWriter writer = new StringWriter())
            {
                var serializer = new XmlSerializer(data.GetType());
                serializer.Serialize(writer, data);

                return writer.ToString();
            }
        }


        /// <summary>
        /// <see cref="IStorageSerializer.Serialize(TextWriter, object)"/>.
        /// </summary>
        public object Serialize(TextWriter writer, object data)
        {
            var serializer = new XmlSerializer(data.GetType());
            serializer.Serialize(writer, data);

            return writer;
        }
    }
}

using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;


namespace Orleans.Storage
{
    /// <summary>
    /// A default JSON serializer for storage providers.
    /// </summary>
    [DebuggerDisplay("CanStream = {CanStream}, Tag = {Tag}")]
    public class OrleansStorageDefaultJsonSerializer: IStorageSerializer
    {
        /// <summary>
        /// The serializer this uses for reference purposes.
        /// </summary>
        public JsonSerializer Serializer { get; }

        /// <summary>
        /// The settings this serializer uses for reference purposes.
        /// </summary>
        public JsonSerializerSettings Settings { get; }

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
        /// <param name="settings">The settings which was used to construct a JSON serializer.</param>
        /// <param name="tag"><see cref="IStorageSerializer.Tag"/>.</param>
        public OrleansStorageDefaultJsonSerializer(JsonSerializerSettings settings, string tag)
        {
            if(settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if(string.IsNullOrWhiteSpace(tag))
            {
                throw new ArgumentException("The parameter should contain characters.", nameof(tag));
            }

            Serializer = JsonSerializer.Create(settings);
            Settings = settings;
            Tag = tag;
        }


        /// <summary>
        /// <see cref="IStorageSerializer.Serialize(Stream, object)"/>
        /// </summary>
        public object Serialize(Stream dataStream, object data)
        {
            using(var streamWriter = new StreamWriter(dataStream))
            {
                using(JsonTextWriter writer = new JsonTextWriter(streamWriter))
                {
                    Serializer.Serialize(writer, data);
                    writer.Flush();
                }
            }

            return dataStream;
        }


        /// <summary>
        /// <see cref="IStorageSerializer.Serialize(object)"/>
        /// </summary>
        public object Serialize(object data)
        {
            return JsonConvert.SerializeObject(data, Settings);
        }


        /// <summary>
        /// <see cref="IStorageSerializer.Serialize(TextWriter, object)"/>.
        /// </summary>
        public object Serialize(TextWriter writer, object data)
        {
            using(JsonTextWriter jsonWriter = new JsonTextWriter(writer))
            {
                Serializer.Serialize(jsonWriter, data);
                jsonWriter.Flush();
            }

            return writer;
        }
    }
}

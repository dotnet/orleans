using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;


namespace Orleans.Storage
{
    /// <summary>
    /// A default JSON deserializer for storage providers.
    /// </summary>
    [DebuggerDisplay("CanStream = {CanStream}, Tag = {Tag}")]
    public class OrleansStorageDefaultJsonDeserializer: IStorageDeserializer
    {
        /// <summary>
        /// The deserializer this uses for reference purposes.
        /// </summary>
        public JsonSerializer Deserializer { get; }

        /// <summary>
        /// The settings this deserializer uses for reference purposes.
        /// </summary>
        public JsonSerializerSettings Settings { get; }

        /// <summary>
        /// <see cref="IStorageDeserializer.CanStream"/>
        /// </summary>
        public bool CanStream { get; } = true;

        /// <summary>
        /// <see cref="IStorageDeserializer.Tag"/>
        /// </summary>
        public string Tag { get; }


        /// <summary>
        /// Constructs this serializer from the given parameters.
        /// </summary>
        /// <param name="settings">The settings which was used to construct a JSON deserializer.</param>
        /// <param name="tag"><see cref="IStorageDeserializer.Tag"/>.</param>
        public OrleansStorageDefaultJsonDeserializer(JsonSerializerSettings settings, string tag)
        {
            if(settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if(string.IsNullOrWhiteSpace(tag))
            {
                throw new ArgumentException("The parameter should contain characters.", nameof(tag));
            }

            Deserializer = JsonSerializer.Create(settings);
            Settings = settings;
            Tag = tag;
        }


        /// <summary>
        /// <see cref="IStorageDeserializer.Deserialize(Stream, Type)"/>.
        /// </summary>
        public object Deserialize(Stream dataStream, Type grainStateType)
        {
            using(var streamReader = new StreamReader(dataStream))
            {
                using(var reader = new JsonTextReader(streamReader))
                {
                    return Deserializer.Deserialize(reader, grainStateType);
                }
            }
        }


        /// <summary>
        /// <see cref="IStorageDeserializer.Deserialize(object, Type)"/>.
        /// </summary>
        public object Deserialize(object data, Type grainStateType)
        {
            return JsonConvert.DeserializeObject((string)data, grainStateType, Settings);
        }


        /// <summary>
        /// <see cref="IStorageDeserializer.Deserialize(TextReader, Type)"/>.
        /// </summary>
        public object Deserialize(TextReader reader, Type grainStateType)
        {
            using(var jsonReader = new JsonTextReader(reader))
            {
                return Deserializer.Deserialize(jsonReader, grainStateType);
            }
        }
    }
}

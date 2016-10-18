using System;
using System.Net;
using System.Runtime.Serialization.Formatters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    using Orleans.Providers;

    public class OrleansJsonSerializer : IExternalSerializer
    {
        public const string UseFullAssemblyNamesProperty = "UseFullAssemblyNames";
        public const string IndentJsonProperty = "IndentJSON";
        private static JsonSerializerSettings defaultSettings;
        private Logger logger;

        static OrleansJsonSerializer()
        {
            defaultSettings = GetDefaultSerializerSettings();
        }

        /// <summary>
        /// Returns the default serializer settings.
        /// </summary>
        /// <returns>The default serializer settings.</returns>
        public static JsonSerializerSettings GetDefaultSerializerSettings()
        {
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects,
                DateFormatHandling = DateFormatHandling.IsoDateFormat,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
                ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
                TypeNameAssemblyFormat = FormatterAssemblyStyle.Simple,
                Formatting = Formatting.None
            };

            settings.Converters.Add(new IPAddressConverter());
            settings.Converters.Add(new IPEndPointConverter());
            settings.Converters.Add(new GrainIdConverter());
            settings.Converters.Add(new SiloAddressConverter());
            settings.Converters.Add(new UniqueKeyConverter());

            return settings;
        }

        /// <summary>
        /// Customises the given serializer settings using provider configuration.
        /// Can be used by any provider, allowing the users to use a standard set of configuration attributes.
        /// </summary>
        /// <param name="settings">The settings to update.</param>
        /// <param name="config">The provider config.</param>
        /// <returns>The updated <see cref="JsonSerializerSettings" />.</returns>
        public static JsonSerializerSettings UpdateSerializerSettings(JsonSerializerSettings settings, IProviderConfiguration config)
        {
            if (config.Properties.ContainsKey(UseFullAssemblyNamesProperty))
            {
                bool useFullAssemblyNames;
                if (bool.TryParse(config.Properties[UseFullAssemblyNamesProperty], out useFullAssemblyNames) && useFullAssemblyNames)
                {
                    settings.TypeNameAssemblyFormat = FormatterAssemblyStyle.Full;
                }
            }

            if (config.Properties.ContainsKey(IndentJsonProperty))
            {
                bool indentJson;
                if (bool.TryParse(config.Properties[IndentJsonProperty], out indentJson) && indentJson)
                {
                    settings.Formatting = Formatting.Indented;
                }
            }
            return settings;
        }

        /// <summary>
        /// Initializes the serializer
        /// </summary>
        /// <param name="logger">The logger to use to capture any serialization events</param>
        public void Initialize(Logger logger)
        {
            this.logger = logger;
        }

        /// <summary>
        /// Informs the serialization manager whether this serializer supports the type for serialization.
        /// </summary>
        /// <param name="itemType">The type of the item to be serialized</param>
        /// <returns>A value indicating whether the item can be serialized.</returns>
        public bool IsSupportedType(Type itemType)
        {
            return true;
        }

        /// <summary>
        /// Creates a deep copy of an object
        /// </summary>
        /// <param name="source">The source object to be copy</param>
        /// <returns>The copy that was created</returns>
        public object DeepCopy(object source)
        {
            if (source == null)
            {
                return null;
            }

            var writer = new BinaryTokenStreamWriter();
            Serialize(source, writer, source.GetType());
            var retVal = Deserialize(source.GetType(), new BinaryTokenStreamReader(writer.ToByteArray()));
            return retVal;
        }

        /// <summary>
        /// Deserializes an object from a binary stream
        /// </summary>
        /// <param name="expectedType">The type that is expected to be deserialized</param>
        /// <param name="reader">The <see cref="BinaryTokenStreamReader"/></param>
        /// <returns>The deserialized object</returns>
        public object Deserialize(Type expectedType, BinaryTokenStreamReader reader)
        {
            if (reader == null)
            {
                throw new ArgumentNullException("reader");
            }

            var str = reader.ReadString();
            return JsonConvert.DeserializeObject(str, expectedType, defaultSettings);
        }

        /// <summary>
        /// Serializes an object to a binary stream
        /// </summary>
        /// <param name="item">The object to serialize</param>
        /// <param name="writer">The <see cref="BinaryTokenStreamWriter"/></param>
        /// <param name="expectedType">The type the deserializer should expect</param>
        public void Serialize(object item, BinaryTokenStreamWriter writer, Type expectedType)
        {
            if (writer == null)
            {
                throw new ArgumentNullException("writer");
            }

            if (item == null)
            {
                writer.WriteNull();
                return;
            }

            var str = JsonConvert.SerializeObject(item, expectedType, defaultSettings);
            writer.Write(str);
        }
    }

    #region JsonConverters

    internal class IPAddressConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return (objectType == typeof(IPAddress));
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            IPAddress ip = (IPAddress)value;
            writer.WriteValue(ip.ToString());
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JToken token = JToken.Load(reader);
            return IPAddress.Parse(token.Value<string>());
        }
    }

    internal class GrainIdConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return (objectType == typeof(GrainId));
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            GrainId id = (GrainId)value;
            writer.WriteStartObject();
            writer.WritePropertyName("GrainId");
            writer.WriteValue(id.ToParsableString());
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JObject jo = JObject.Load(reader);
            GrainId grainId = GrainId.FromParsableString(jo["GrainId"].ToObject<string>());
            return grainId;
        }
    }

    internal class SiloAddressConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return (objectType == typeof(SiloAddress));
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            SiloAddress addr = (SiloAddress)value;
            writer.WriteStartObject();
            writer.WritePropertyName("SiloAddress");
            writer.WriteValue(addr.ToParsableString());
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JObject jo = JObject.Load(reader);
            SiloAddress addr = SiloAddress.FromParsableString(jo["SiloAddress"].ToObject<string>());
            return addr;
        }
    }

    internal class UniqueKeyConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return (objectType == typeof(UniqueKey));
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            UniqueKey key = (UniqueKey)value;
            writer.WriteStartObject();
            writer.WritePropertyName("UniqueKey");
            writer.WriteValue(key.ToHexString());
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JObject jo = JObject.Load(reader);
            UniqueKey addr = UniqueKey.Parse(jo["UniqueKey"].ToObject<string>());
            return addr;
        }
    }

    internal class IPEndPointConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return (objectType == typeof(IPEndPoint));
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            IPEndPoint ep = (IPEndPoint)value;
            writer.WriteStartObject();
            writer.WritePropertyName("Address");
            serializer.Serialize(writer, ep.Address);
            writer.WritePropertyName("Port");
            writer.WriteValue(ep.Port);
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JObject jo = JObject.Load(reader);
            IPAddress address = jo["Address"].ToObject<IPAddress>(serializer);
            int port = jo["Port"].Value<int>();
            return new IPEndPoint(address, port);
        }
    }
    #endregion
}

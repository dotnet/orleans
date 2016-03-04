using System;
using System.Net;
using System.Runtime.Serialization.Formatters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    internal class OrleansJsonSerializer : IExternalSerializer
    {
        private static JsonSerializerSettings defaultSettings;
        private TraceLogger logger;

        /// <summary>
        /// Returns a configured <see cref="JsonSerializerSettings"/> 
        /// </summary>
        /// <returns></returns>
        internal static JsonSerializerSettings GetDefaultSerializerSettings()
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

        static OrleansJsonSerializer()
        {
            defaultSettings = GetDefaultSerializerSettings();
        }

        /// <summary>
        /// Initializes the serializer
        /// </summary>
        /// <param name="logger">The logger to use to capture any serialization events</param>
        public void Initialize(TraceLogger logger)
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

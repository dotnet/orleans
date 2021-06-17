using System;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.Runtime;
using Orleans.GrainReferences;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Serialization
{
    public class OrleansJsonSerializer
    {
        public const string UseFullAssemblyNamesProperty = "UseFullAssemblyNames";
        public const string IndentJsonProperty = "IndentJSON";
        public const string TypeNameHandlingProperty = "TypeNameHandling";
        private readonly Lazy<JsonSerializerSettings> settings;

        public OrleansJsonSerializer(IServiceProvider services)
        {
            this.settings = new Lazy<JsonSerializerSettings>(() =>
            {
                return GetDefaultSerializerSettings(services);
            });
        }

        /// <summary>
        /// Returns the default serializer settings.
        /// </summary>
        /// <returns>The default serializer settings.</returns>
        public static JsonSerializerSettings GetDefaultSerializerSettings(IServiceProvider services)
        {
            var typeResolver = services.GetRequiredService<TypeResolver>();
            var serializationBinder = new OrleansJsonSerializationBinder(typeResolver);
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects,
                DateFormatHandling = DateFormatHandling.IsoDateFormat,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
                ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
                TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
                Formatting = Formatting.None,
                SerializationBinder = serializationBinder
            };

            settings.Converters.Add(new IPAddressConverter());
            settings.Converters.Add(new IPEndPointConverter());
            settings.Converters.Add(new GrainIdConverter());
            settings.Converters.Add(new SiloAddressConverter());
            settings.Converters.Add(new UniqueKeyConverter());
            settings.Converters.Add(new GrainReferenceJsonConverter(services.GetRequiredService<GrainReferenceActivator>()));

            return settings;
        }

        public static JsonSerializerSettings UpdateSerializerSettings(JsonSerializerSettings settings, bool useFullAssemblyNames, bool indentJson, TypeNameHandling? typeNameHandling)
        {
            if (useFullAssemblyNames)
            {
                settings.TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Full;
            }

            if (indentJson)
            {
                settings.Formatting = Formatting.Indented;
            }

            if (typeNameHandling.HasValue)
            {
                settings.TypeNameHandling = typeNameHandling.Value;
            }
           
            return settings;
        }

        /// <inheritdoc />
        public bool IsSupportedType(Type itemType)
        {
            return true;
        }

        /// <inheritdoc />
        public object Deserialize(Type expectedType, string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            return JsonConvert.DeserializeObject(input, expectedType, this.settings.Value);
        }

        /// <summary>
        /// Serializes an object to a binary stream
        /// </summary>
        /// <param name="item">The object to serialize</param>
        /// <param name="expectedType">The type the deserializer should expect</param>
        public string Serialize(object item, Type expectedType) => JsonConvert.SerializeObject(item, expectedType, this.settings.Value);
    }

    public class IPAddressConverter : JsonConverter
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

    public class GrainIdConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return (objectType == typeof(GrainId));
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            GrainId id = (GrainId)value;
            writer.WriteStartObject();
            writer.WritePropertyName("Type");
            writer.WriteValue(id.Type.ToStringUtf8());
            writer.WritePropertyName("Key");
            writer.WriteValue(id.Key.ToStringUtf8());
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JObject jo = JObject.Load(reader);
            GrainId grainId = GrainId.Create(jo["Type"].ToObject<string>(), jo["Key"].ToObject<string>());
            return grainId;
        }
    }

    public class SiloAddressConverter : JsonConverter
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

    public class UniqueKeyConverter : JsonConverter
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
            UniqueKey addr = UniqueKey.Parse(jo["UniqueKey"].ToObject<string>().AsSpan());
            return addr;
        }
    }

    public class IPEndPointConverter : JsonConverter
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

    public class GrainReferenceJsonConverter : JsonConverter
    {
        private static readonly Type AddressableType = typeof(IAddressable);
        private readonly GrainReferenceActivator referenceActivator;

        public GrainReferenceJsonConverter(GrainReferenceActivator referenceActivator)
        {
            this.referenceActivator = referenceActivator;
        }

        public override bool CanConvert(Type objectType)
        {
            return AddressableType.IsAssignableFrom(objectType);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var val = (GrainReference)value;
            writer.WriteStartObject();
            writer.WritePropertyName("Id");
            writer.WriteStartObject();
            writer.WritePropertyName("Type");
            writer.WriteValue(val.GrainId.Type.ToStringUtf8());
            writer.WritePropertyName("Key");
            writer.WriteValue(val.GrainId.Key.ToStringUtf8());
            writer.WriteEndObject();
            writer.WritePropertyName("Interface");
            writer.WriteValue(val.InterfaceType.ToStringUtf8());
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JObject jo = JObject.Load(reader);
            var id = jo["Id"];
            GrainId grainId = GrainId.Create(id["Type"].ToObject<string>(), id["Key"].ToObject<string>());
            var iface = GrainInterfaceType.Create(jo["Interface"].ToString());
            return this.referenceActivator.CreateReference(grainId, iface);
        }
    }
}

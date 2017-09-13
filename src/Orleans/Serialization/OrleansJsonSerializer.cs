using System;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.CodeGeneration;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    using Orleans.Providers;
    
    public class OrleansJsonSerializer : IExternalSerializer
    {
        public const string UseFullAssemblyNamesProperty = "UseFullAssemblyNames";
        public const string IndentJsonProperty = "IndentJSON";
        public const string TypeNameHandlingProperty = "TypeNameHandling";
        private readonly JsonSerializerSettings settings;
        private Logger logger;

        public OrleansJsonSerializer(SerializationManager serializationManager, IGrainFactory grainFactory)
        {
            this.settings = GetDefaultSerializerSettings(serializationManager, grainFactory);
        }

        /// <summary>
        /// Returns the default serializer settings.
        /// </summary>
        /// <returns>The default serializer settings.</returns>
        public static JsonSerializerSettings GetDefaultSerializerSettings(SerializationManager serializationManager, IGrainFactory grainFactory)
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
#if !NETSTANDARD_TODO
                TypeNameAssemblyFormat = FormatterAssemblyStyle.Simple,

                // Types such as GrainReference need context during deserialization, so provide that context now.
                Context = new StreamingContext(StreamingContextStates.All, new SerializationContext(serializationManager)),
#endif
                Formatting = Formatting.None
            };

            settings.Converters.Add(new IPAddressConverter());
            settings.Converters.Add(new IPEndPointConverter());
            settings.Converters.Add(new GrainIdConverter());
            settings.Converters.Add(new SiloAddressConverter());
            settings.Converters.Add(new UniqueKeyConverter());
            settings.Converters.Add(new GrainReferenceConverter(grainFactory));

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
#if !NETSTANDARD_TODO
                    settings.TypeNameAssemblyFormat = FormatterAssemblyStyle.Full;
#endif
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

            if (config.Properties.ContainsKey(TypeNameHandlingProperty))
            {
                TypeNameHandling typeNameHandling;
                if (Enum.TryParse<TypeNameHandling>(config.Properties[TypeNameHandlingProperty], out typeNameHandling))
                {
                    settings.TypeNameHandling = typeNameHandling;
                }
            }
            return settings;
        }

        /// <inheritdoc />
        public void Initialize(Logger logger)
        {
            this.logger = logger;
        }

        /// <inheritdoc />
        public bool IsSupportedType(Type itemType)
        {
            return true;
        }

        /// <inheritdoc />
        public object DeepCopy(object source, ICopyContext context)
        {
            if (source == null)
            {
                return null;
            }

            var serializationContext = new SerializationContext(context.SerializationManager)
            {
                StreamWriter = new BinaryTokenStreamWriter()
            };
            
            Serialize(source, serializationContext, source.GetType());
            var deserializationContext = new DeserializationContext(context.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(serializationContext.StreamWriter.ToBytes())
            };

            var retVal = Deserialize(source.GetType(), deserializationContext);
            serializationContext.StreamWriter.ReleaseBuffers();
            return retVal;
        }

        /// <inheritdoc />
        public object Deserialize(Type expectedType, IDeserializationContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var reader = context.StreamReader;
            var str = reader.ReadString();
            return JsonConvert.DeserializeObject(str, expectedType, this.settings);
        }

        /// <summary>
        /// Serializes an object to a binary stream
        /// </summary>
        /// <param name="item">The object to serialize</param>
        /// <param name="context">The serialization context.</param>
        /// <param name="expectedType">The type the deserializer should expect</param>
        public void Serialize(object item, ISerializationContext context, Type expectedType)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var writer = context.StreamWriter;
            if (item == null)
            {
                writer.WriteNull();
                return;
            }

            var str = JsonConvert.SerializeObject(item, expectedType, this.settings);
            writer.Write(str);
        }
    }

#region JsonConverters

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
            UniqueKey addr = UniqueKey.Parse(jo["UniqueKey"].ToObject<string>());
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

    public class GrainReferenceConverter : JsonConverter
    {
        private static readonly Type AddressableType = typeof(IAddressable);
        private readonly IGrainFactory grainFactory;
        private readonly JsonSerializer internalSerializer;

        public GrainReferenceConverter(IGrainFactory grainFactory)
        {
            this.grainFactory = grainFactory;

            // Create a serializer for internal serialization which does not have a specified GrainReference serializer.
            // This internal serializer will use GrainReference's ISerializable implementation for serialization and deserialization.
            this.internalSerializer = JsonSerializer.Create(new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All,
                PreserveReferencesHandling = PreserveReferencesHandling.None,
                DateFormatHandling = DateFormatHandling.IsoDateFormat,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
                ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
                Formatting = Formatting.None,
                Converters =
                {
                    new IPAddressConverter(),
                    new IPEndPointConverter(),
                    new GrainIdConverter(),
                    new SiloAddressConverter(),
                    new UniqueKeyConverter()
                }
            });
        }

        public override bool CanConvert(Type objectType)
        {
            return AddressableType.IsAssignableFrom(objectType);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            // Serialize the grain reference using the internal serializer.
            this.internalSerializer.Serialize(writer, value);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            // Deserialize using the internal serializer which will use the concrete GrainReference implementation's
            // ISerializable constructor.
            var result = this.internalSerializer.Deserialize(reader, objectType);
            var grainRef = result as IAddressable;
            if (grainRef == null) return result;

            // Bind the deserialized grain reference to the runtime.
            this.grainFactory.BindGrainReference(grainRef);
            return grainRef;
        }
    }

    #endregion
}
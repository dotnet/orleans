using System;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.Runtime;
using Orleans.GrainReferences;
using Orleans.Serialization.TypeSystem;
using System.Globalization;

namespace Orleans.Serialization
{
    /// <summary>
    /// Utility class for configuring <see cref="JsonSerializerSettings"/> to support Orleans types.
    /// </summary>
    public class OrleansJsonSerializer
    {
        public const string UseFullAssemblyNamesProperty = "UseFullAssemblyNames";
        public const string IndentJsonProperty = "IndentJSON";
        public const string TypeNameHandlingProperty = "TypeNameHandling";
        private readonly Lazy<JsonSerializerSettings> settings;

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansJsonSerializer"/> class.
        /// </summary>
        /// <param name="services">The service provider.</param>
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
        /// <param name="services">
        /// The service provider.
        /// </param>
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
            settings.Converters.Add(new ActivationIdConverter());
            settings.Converters.Add(new SiloAddressJsonConverter());
            settings.Converters.Add(new MembershipVersionJsonConverter());
            settings.Converters.Add(new UniqueKeyConverter());
            settings.Converters.Add(new GrainReferenceJsonConverter(services.GetRequiredService<GrainReferenceActivator>()));

            return settings;
        }

        /// <summary>
        /// Updates the provided serializer settings with the specified options.
        /// </summary>
        /// <param name="settings">The settings.</param>
        /// <param name="useFullAssemblyNames">if set to <c>true</c>, use full assembly-qualified names when formatting type names.</param>
        /// <param name="indentJson">if set to <c>true</c>, indent the formatted JSON.</param>
        /// <param name="typeNameHandling">The type name handling options.</param>
        /// <returns>The provided serializer settings.</returns>
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

        /// <summary>
        /// Deserializes an object of the specified expected type from the provided input.
        /// </summary>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="input">The input.</param>
        /// <returns>The deserialized object.</returns>
        public object Deserialize(Type expectedType, string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            return JsonConvert.DeserializeObject(input, expectedType, this.settings.Value);
        }

        /// <summary>
        /// Serializes an object to a JSON string.
        /// </summary>
        /// <param name="item">The object to serialize.</param>
        /// <param name="expectedType">The type the deserializer should expect.</param>
        public string Serialize(object item, Type expectedType) => JsonConvert.SerializeObject(item, expectedType, this.settings.Value);
    }

    /// <summary>
    /// <see cref="Newtonsoft.Json.JsonConverter" /> implementation for <see cref="IPAddress"/>.
    /// </summary>
    /// <seealso cref="Newtonsoft.Json.JsonConverter" />
    public class IPAddressConverter : JsonConverter
    {
        /// <inheritdoc/>
        public override bool CanConvert(Type objectType)
        {
            return (objectType == typeof(IPAddress));
        }

        /// <inheritdoc/>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            IPAddress ip = (IPAddress)value;
            writer.WriteValue(ip.ToString());
        }

        /// <inheritdoc/>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JToken token = JToken.Load(reader);
            return IPAddress.Parse(token.Value<string>());
        }
    }

    /// <summary>
    /// <see cref="Newtonsoft.Json.JsonConverter" /> implementation for <see cref="GrainId"/>.
    /// </summary>
    /// <seealso cref="Newtonsoft.Json.JsonConverter" />
    public class GrainIdConverter : JsonConverter
    {
        /// <inheritdoc/>
        public override bool CanConvert(Type objectType) => objectType == typeof(GrainId);

        /// <inheritdoc/>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            GrainId id = (GrainId)value;
            writer.WriteStartObject();
            writer.WritePropertyName("Type");
            writer.WriteValue(id.Type.ToString());
            writer.WritePropertyName("Key");
            writer.WriteValue(id.Key.ToString());
            writer.WriteEndObject();
        }

        /// <inheritdoc/>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JObject jo = JObject.Load(reader);
            GrainId grainId = GrainId.Create(jo["Type"].ToObject<string>(), jo["Key"].ToObject<string>());
            return grainId;
        }
    }

    /// <summary>
    /// <see cref="Newtonsoft.Json.JsonConverter" /> implementation for <see cref="ActivationId"/>.
    /// </summary>
    /// <seealso cref="Newtonsoft.Json.JsonConverter" />
    public class ActivationIdConverter : JsonConverter
    {
        /// <inheritdoc/>
        public override bool CanConvert(Type objectType) => objectType == typeof(ActivationId);

        /// <inheritdoc/>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            ActivationId id = (ActivationId)value;
            writer.WriteValue(id.ToParsableString());
        }

        /// <inheritdoc/>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            return reader.Value switch
            {
                string { Length: > 0 } str => ActivationId.FromParsableString(str),
                _ => default
            };
        }
    }

    /// <summary>
    /// <see cref="Newtonsoft.Json.JsonConverter" /> implementation for <see cref="SiloAddress"/>.
    /// </summary>
    /// <seealso cref="Newtonsoft.Json.JsonConverter" />
    public class SiloAddressJsonConverter : JsonConverter
    {
        /// <inheritdoc/>
        public override bool CanConvert(Type objectType)
        {
            return (objectType == typeof(SiloAddress));
        }

        /// <inheritdoc/>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            SiloAddress addr = (SiloAddress)value;
            writer.WriteValue(addr.ToParsableString());
        }

        /// <inheritdoc/>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            switch (reader.TokenType)
            {
                case JsonToken.StartObject:
                    var jo = JObject.Load(reader);
                    return SiloAddress.FromParsableString(jo["SiloAddress"].ToObject<string>());
                case JsonToken.String:
                    return SiloAddress.FromParsableString(reader.Value as string);
            }

            return null;
        }
    }

    /// <summary>
    /// <see cref="Newtonsoft.Json.JsonConverter" /> implementation for <see cref="MembershipVersion"/>.
    /// </summary>
    /// <seealso cref="Newtonsoft.Json.JsonConverter" />
    public class MembershipVersionJsonConverter : JsonConverter
    {
        /// <inheritdoc/>
        public override bool CanConvert(Type objectType) => objectType == typeof(MembershipVersion);

        /// <inheritdoc/>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            MembershipVersion typedValue = (MembershipVersion)value;
            writer.WriteValue(typedValue.Value);
        }

        /// <inheritdoc/>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            return reader.Value switch
            {
                long l => new MembershipVersion(l),
                _ => default(MembershipVersion)
            };
        }
    }

    /// <summary>
    /// <see cref="Newtonsoft.Json.JsonConverter" /> implementation for <see cref="UniqueKey"/>.
    /// </summary>
    /// <seealso cref="Newtonsoft.Json.JsonConverter" />
    public class UniqueKeyConverter : JsonConverter
    {
        /// <inheritdoc/>
        public override bool CanConvert(Type objectType)
        {
            return (objectType == typeof(UniqueKey));
        }

        /// <inheritdoc/>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            UniqueKey key = (UniqueKey)value;
            writer.WriteStartObject();
            writer.WritePropertyName("UniqueKey");
            writer.WriteValue(key.ToHexString());
            writer.WriteEndObject();
        }

        /// <inheritdoc/>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JObject jo = JObject.Load(reader);
            UniqueKey addr = UniqueKey.Parse(jo["UniqueKey"].ToObject<string>().AsSpan());
            return addr;
        }
    }

    /// <summary>
    /// <see cref="Newtonsoft.Json.JsonConverter" /> implementation for <see cref="IPEndPoint"/>.
    /// </summary>
    /// <seealso cref="Newtonsoft.Json.JsonConverter" />
    public class IPEndPointConverter : JsonConverter
    {
        /// <inheritdoc/>
        public override bool CanConvert(Type objectType)
        {
            return (objectType == typeof(IPEndPoint));
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JObject jo = JObject.Load(reader);
            IPAddress address = jo["Address"].ToObject<IPAddress>(serializer);
            int port = jo["Port"].Value<int>();
            return new IPEndPoint(address, port);
        }
    }

    /// <summary>
    /// <see cref="Newtonsoft.Json.JsonConverter" /> implementation for <see cref="GrainReference"/>.
    /// </summary>
    /// <seealso cref="Newtonsoft.Json.JsonConverter" />
    public class GrainReferenceJsonConverter : JsonConverter
    {
        private static readonly Type AddressableType = typeof(IAddressable);
        private readonly GrainReferenceActivator referenceActivator;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainReferenceJsonConverter"/> class.
        /// </summary>
        /// <param name="referenceActivator">The grain reference activator.</param>
        public GrainReferenceJsonConverter(GrainReferenceActivator referenceActivator)
        {
            this.referenceActivator = referenceActivator;
        }

        /// <inheritdoc/>
        public override bool CanConvert(Type objectType)
        {
            return AddressableType.IsAssignableFrom(objectType);
        }

        /// <inheritdoc/>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var val = (GrainReference)value;
            writer.WriteStartObject();
            writer.WritePropertyName("Id");
            writer.WriteStartObject();
            writer.WritePropertyName("Type");
            writer.WriteValue(val.GrainId.Type.ToString());
            writer.WritePropertyName("Key");
            writer.WriteValue(val.GrainId.Key.ToString());
            writer.WriteEndObject();
            writer.WritePropertyName("Interface");
            writer.WriteValue(val.InterfaceType.ToString());
            writer.WriteEndObject();
        }

        /// <inheritdoc/>
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

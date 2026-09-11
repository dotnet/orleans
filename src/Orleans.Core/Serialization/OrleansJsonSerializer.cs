using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.GrainReferences;

namespace Orleans.Serialization
{

    /// <summary>
    /// Serializes and deserializes values using Newtonsoft.Json settings configured for Orleans types.
    /// </summary>
    public class OrleansJsonSerializer
    {
        /// <summary>
        /// The configuration property name for selecting full assembly-qualified type names.
        /// </summary>
        public const string UseFullAssemblyNamesProperty = "UseFullAssemblyNames";

        /// <summary>
        /// The configuration property name for selecting indented JSON formatting.
        /// </summary>
        public const string IndentJsonProperty = "IndentJSON";

        /// <summary>
        /// The configuration property name for selecting how type names are included in JSON.
        /// </summary>
        public const string TypeNameHandlingProperty = "TypeNameHandling";
        private readonly JsonSerializerSettings settings;

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansJsonSerializer"/> class.
        /// </summary>
        /// <param name="options">The configured JSON serializer options.</param>
        public OrleansJsonSerializer(IOptions<OrleansJsonSerializerOptions> options)
        {
            this.settings = options.Value.JsonSerializerSettings;
        }

        /// <summary>
        /// Deserializes an object of the specified expected type from the provided input.
        /// </summary>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="input">The input.</param>
        /// <returns>The deserialized object.</returns>
        public object? Deserialize(Type expectedType, string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            return JsonConvert.DeserializeObject(input, expectedType, this.settings);
        }

        /// <summary>
        /// Deserializes an object of the specified expected type from the provided stream.
        /// </summary>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="input">The input stream.</param>
        /// <returns>The deserialized object.</returns>
        public object? Deserialize(Type expectedType, Stream input)
        {
            using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            using var jsonReader = new JsonTextReader(reader);
            var serializer = JsonSerializer.Create(this.settings);
            return serializer.Deserialize(jsonReader, expectedType);
        }

        /// <summary>
        /// Serializes an object to a JSON string.
        /// </summary>
        /// <param name="item">The object to serialize.</param>
        /// <param name="expectedType">The type the deserializer should expect.</param>
        /// <returns>The JSON representation of <paramref name="item"/>.</returns>
        public string Serialize(object? item, Type expectedType) => JsonConvert.SerializeObject(item, expectedType, this.settings);

        /// <summary>
        /// Serializes an object to a stream.
        /// </summary>
        /// <param name="item">The object to serialize.</param>
        /// <param name="expectedType">The type the deserializer should expect.</param>
        /// <param name="destination">The destination stream.</param>
        public void Serialize(object? item, Type expectedType, Stream destination)
        {
            using var writer = new StreamWriter(destination, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1024, leaveOpen: true);
            using var jsonWriter = new JsonTextWriter(writer);
            var serializer = JsonSerializer.Create(this.settings);
            serializer.Serialize(jsonWriter, item, expectedType);
        }
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
        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            IPAddress ip = (IPAddress)value!;
            writer.WriteValue(ip.ToString());
        }

        /// <inheritdoc/>
        public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            JToken token = JToken.Load(reader);
            return IPAddress.Parse(token.Value<string>()!);
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
        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            GrainId id = (GrainId)value!;
            writer.WriteStartObject();
            writer.WritePropertyName("Type");
            writer.WriteValue(id.Type.ToString());
            writer.WritePropertyName("Key");
            writer.WriteValue(id.Key.ToString());
            writer.WriteEndObject();
        }

        /// <inheritdoc/>
        public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            JObject jo = JObject.Load(reader);
            GrainId grainId = GrainId.Create(jo["Type"]!.ToObject<string>()!, jo["Key"]!.ToObject<string>()!);
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
        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            ActivationId id = (ActivationId)value!;
            writer.WriteValue(id.ToParsableString());
        }

        /// <inheritdoc/>
        public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
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
        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            SiloAddress addr = (SiloAddress)value!;
            writer.WriteValue(addr.ToParsableString());
        }

        /// <inheritdoc/>
        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            switch (reader.TokenType)
            {
                case JsonToken.StartObject:
                    var jo = JObject.Load(reader);
                    return SiloAddress.FromParsableString(jo["SiloAddress"]!.ToObject<string>()!);
                case JsonToken.String:
                    return SiloAddress.FromParsableString((reader.Value as string)!);
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
        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            MembershipVersion typedValue = (MembershipVersion)value!;
            writer.WriteValue(typedValue.Value);
        }

        /// <inheritdoc/>
        public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            return reader.Value switch
            {
                long l => new MembershipVersion(l),
                _ => default
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
        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            UniqueKey key = (UniqueKey)value!;
            writer.WriteStartObject();
            writer.WritePropertyName("UniqueKey");
            writer.WriteValue(key.ToHexString());
            writer.WriteEndObject();
        }

        /// <inheritdoc/>
        public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            JObject jo = JObject.Load(reader);
            UniqueKey addr = UniqueKey.Parse(jo["UniqueKey"]!.ToObject<string>()!.AsSpan());
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
        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            IPEndPoint ep = (IPEndPoint)value!;
            writer.WriteStartObject();
            writer.WritePropertyName("Address");
            serializer.Serialize(writer, ep.Address);
            writer.WritePropertyName("Port");
            writer.WriteValue(ep.Port);
            writer.WriteEndObject();
        }

        /// <inheritdoc/>
        public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            JObject jo = JObject.Load(reader);
            IPAddress address = jo["Address"]!.ToObject<IPAddress>(serializer)!;
            int port = jo["Port"]!.Value<int>();
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
        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            var val = ((IAddressable)value!).AsReference();
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
            writer.WritePropertyName("ServiceId");
            writer.WriteValue(val.UniversalReference.ServiceId);
            writer.WritePropertyName("Binding");
            writer.WriteValue((byte)val.UniversalReference.Binding);
            if (val.UniversalReference.ClusterId is { } clusterId)
            {
                writer.WritePropertyName("ClusterId");
                writer.WriteValue(clusterId);
            }

            writer.WriteEndObject();
        }

        /// <inheritdoc/>
        public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            JObject jo = JObject.Load(reader);
            var id = jo["Id"] ?? throw new JsonSerializationException("The grain identity is missing.");
            var grainId = GrainId.Create(id["Type"]!.ToObject<string>()!, id["Key"]!.ToObject<string>()!);
            var encodedInterface = jo["Interface"]?.ToString()
                ?? throw new JsonSerializationException("The grain interface is missing.");
            var interfaceType = string.IsNullOrWhiteSpace(encodedInterface) ? default : GrainInterfaceType.Create(encodedInterface);
            var serviceIdToken = jo["ServiceId"];
            var bindingToken = jo["Binding"];
            if (serviceIdToken is null && bindingToken is null)
            {
                return this.referenceActivator.CreateReference(grainId, interfaceType);
            }

            if (serviceIdToken is null || bindingToken is null)
            {
                throw new JsonSerializationException("The universal reference service identity and binding must both be present.");
            }

            var serviceId = serviceIdToken.ToObject<string>()!;
            var binding = (UniversalReferenceBinding)bindingToken.ToObject<byte>();
            if (binding is not (UniversalReferenceBinding.Virtual or UniversalReferenceBinding.Cluster))
            {
                throw new JsonSerializationException($"Unknown universal reference binding '{binding}'.");
            }

            var clusterId = jo["ClusterId"]?.ToObject<string>();
            try
            {
                var reference = new UniversalReference(
                    grainId,
                    interfaceType,
                    serviceId,
                    binding,
                    clusterId);
                return this.referenceActivator.CreateReference(reference);
            }
            catch (ArgumentException exception) when (
                binding == UniversalReferenceBinding.Cluster
                && string.IsNullOrWhiteSpace(clusterId))
            {
                throw new JsonSerializationException(
                    "A cluster-bound universal reference must specify a non-empty ClusterId.",
                    exception);
            }
            catch (ArgumentException exception)
            {
                throw new JsonSerializationException(
                    "Could not deserialize an invalid universal reference.",
                    exception);
            }
        }
    }
}

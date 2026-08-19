#nullable enable

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans.GrainReferences;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    /// <summary>
    /// A <see cref="JsonConverter{T}"/> for <see cref="IAddressable"/> types (grain references).
    /// </summary>
    public sealed class GrainReferenceConverter(GrainReferenceActivator referenceActivator) : JsonConverter<IAddressable>
    {
        private readonly Type _addressableType = typeof(IAddressable);

        /// <inheritdoc />
        public override bool CanConvert(Type typeToConvert) => _addressableType.IsAssignableFrom(typeToConvert);

        /// <inheritdoc />
        public override IAddressable? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray || !reader.Read())
            {
                throw new JsonException($"Could not deserialize {nameof(IAddressable)}.");
            }

            var grainId = JsonSerializer.Deserialize<GrainId>(ref reader, options);
            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"Could not deserialize {nameof(IAddressable)}.");
            }

            var interfaceType = reader.GetString();
            if (!reader.Read()
                || reader.TokenType != JsonTokenType.EndArray
                || grainId.IsDefault
                || interfaceType is null)
            {
                throw new JsonException($"Could not deserialize {nameof(IAddressable)}.");
            }

            var grainInterface = string.IsNullOrEmpty(interfaceType) ? default : GrainInterfaceType.Create(interfaceType);
            var reference = referenceActivator.CreateReference(grainId, grainInterface);
            if (!typeToConvert.IsInstanceOfType(reference))
            {
                throw new JsonException($"Cannot deserialize a grain reference with interface type '{grainInterface}' as {typeToConvert}.");
            }

            return reference;
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, IAddressable value, JsonSerializerOptions options)
        {
            var val = value.AsReference();
            writer.WriteStartArray();
            JsonSerializer.Serialize(writer, val.GrainId, options);
            writer.WriteStringValue(val.InterfaceType.ToString());
            writer.WriteEndArray();
        }
    }
}

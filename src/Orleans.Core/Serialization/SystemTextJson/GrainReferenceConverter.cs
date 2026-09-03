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
    /// <param name="referenceActivator">The activator used to construct grain references during deserialization.</param>
    public sealed class GrainReferenceConverter(GrainReferenceActivator referenceActivator) : JsonConverter<IAddressable>
    {
        private readonly Type _addressableType = typeof(IAddressable);

        private readonly record struct UniversalReferenceJsonData(
            GrainId GrainId,
            string? InterfaceType,
            string? ServiceId,
            UniversalReferenceBinding Binding,
            string? ClusterId);

        /// <inheritdoc />
        public override bool CanConvert(Type typeToConvert) => _addressableType.IsAssignableFrom(typeToConvert);

        /// <inheritdoc />
        public override IAddressable? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray || !reader.Read())
            {
                throw new JsonException($"Could not deserialize {nameof(IAddressable)}.");
            }

            UniversalReference universalReference;
            if (reader.TokenType == JsonTokenType.String)
            {
                var grainId = JsonSerializer.Deserialize<GrainId>(ref reader, options);
                if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                {
                    throw new JsonException($"Could not deserialize {nameof(IAddressable)}.");
                }

                var encodedInterface = reader.GetString();
                var interfaceType = string.IsNullOrEmpty(encodedInterface)
                    ? default
                    : GrainInterfaceType.Create(encodedInterface);
                if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
                {
                    throw new JsonException($"Could not deserialize {nameof(IAddressable)}.");
                }

                var legacyReference = referenceActivator.CreateReference(grainId, interfaceType);
                if (!typeToConvert.IsInstanceOfType(legacyReference))
                {
                    throw new JsonException(
                        $"Cannot deserialize a grain reference with interface type '{interfaceType}' as {typeToConvert}.");
                }

                return legacyReference;
            }

            var data = JsonSerializer.Deserialize<UniversalReferenceJsonData>(ref reader, options);
            var dataInterfaceType = string.IsNullOrEmpty(data.InterfaceType)
                ? default
                : GrainInterfaceType.Create(data.InterfaceType);
            try
            {
                universalReference = new UniversalReference(
                    data.GrainId,
                    dataInterfaceType,
                    data.ServiceId!,
                    data.Binding,
                    data.ClusterId);
            }
            catch (ArgumentException exception)
            {
                throw new JsonException("Could not deserialize an invalid universal reference.", exception);
            }

            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray || universalReference.IsDefault)
            {
                throw new JsonException($"Could not deserialize {nameof(IAddressable)}.");
            }

            var reference = referenceActivator.CreateReference(universalReference);
            if (!typeToConvert.IsInstanceOfType(reference))
            {
                throw new JsonException($"Cannot deserialize a grain reference with interface type '{universalReference.InterfaceType}' as {typeToConvert}.");
            }

            return reference;
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, IAddressable value, JsonSerializerOptions options)
        {
            var val = value.AsReference();
            writer.WriteStartArray();
            var reference = val.UniversalReference;
            var shared = val.Shared;
            if (reference.Binding == UniversalReferenceBinding.Virtual
                && reference.Binding == shared.DefaultBinding
                && string.Equals(reference.ServiceId, shared.ServiceId, StringComparison.Ordinal))
            {
                JsonSerializer.Serialize(writer, reference.GrainId, options);
                writer.WriteStringValue(val.InterfaceType.ToString());
            }
            else
            {
                JsonSerializer.Serialize(
                    writer,
                    new UniversalReferenceJsonData(
                        reference.GrainId,
                        val.InterfaceType.ToString(),
                        reference.ServiceId,
                        reference.Binding,
                        reference.ClusterId),
                    options);
            }

            writer.WriteEndArray();
        }
    }
}

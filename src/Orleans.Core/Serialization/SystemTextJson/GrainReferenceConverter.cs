#nullable enable

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans.GrainReferences;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    internal sealed class GrainReferenceConverter(GrainReferenceActivator referenceActivator) : JsonConverter<IAddressable>
    {
        private readonly Type _addressableType = typeof(IAddressable);

        /// <inheritdoc />
        public override bool CanConvert(Type typeToConvert) => _addressableType.IsAssignableFrom(typeToConvert);

        /// <inheritdoc />
        public override IAddressable? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? type = null, key = null, iface = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var propertyName = reader.GetString();
                    reader.Read();

                    if (propertyName == "Id")
                    {
                        while (reader.Read())
                        {
                            if (reader.TokenType == JsonTokenType.EndObject)
                                break;

                            if (reader.TokenType == JsonTokenType.PropertyName)
                            {
                                var idProperty = reader.GetString();
                                reader.Read();

                                if (idProperty == "Type") type = reader.GetString();
                                if (idProperty == "Key") key = reader.GetString();
                            }
                        }
                    }
                    else if (propertyName == "Interface")
                    {
                        iface = reader.GetString();
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var grainId = GrainId.Create(type, key);
            var grainInterface = string.IsNullOrWhiteSpace(iface) ? default : GrainInterfaceType.Create(iface);
            return referenceActivator.CreateReference(grainId, grainInterface);
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, IAddressable value, JsonSerializerOptions options)
        {
            var val = value.AsReference();
            writer.WriteStartObject();
            writer.WriteStartObject("Id");
            writer.WriteString("Type", val.GrainId.Type.AsSpan());
            writer.WriteString("Key", val.GrainId.Key.AsSpan());
            writer.WriteEndObject();
            writer.WriteString("Interface", val.InterfaceType.ToString());
            writer.WriteEndObject();
        }
    }
}

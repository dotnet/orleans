using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Orleans.Metadata;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization;

/// <summary>
/// A serialization codec which uses <see cref="JsonSerializer"/>.
/// </summary>
[Alias(WellKnownAlias)]
public class JsonCodec : IGeneralizedCodec, IGeneralizedCopier, ITypeFilter
{
    private static readonly Type SelfType = typeof(JsonCodec);
    private readonly ICodecSelector[] _serializableTypeSelectors;
    private readonly ICopierSelector[] _copyableTypeSelectors;
    private readonly JsonCodecOptions _options;

    /// <summary>
    /// The well-known type alias for this codec.
    /// </summary>
    public const string WellKnownAlias = "json";

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonCodec"/> class.
    /// </summary>
    /// <param name="serializableTypeSelectors">Filters used to indicate which types should be serialized by this codec.</param>
    /// <param name="copyableTypeSelectors">Filters used to indicate which types should be copied by this codec.</param>
    /// <param name="options">The JSON codec options.</param>
    public JsonCodec(
        IEnumerable<ICodecSelector> serializableTypeSelectors,
        IEnumerable<ICopierSelector> copyableTypeSelectors,
        IOptions<JsonCodecOptions> options)
    {
        _serializableTypeSelectors = serializableTypeSelectors.Where(t => string.Equals(t.CodecName, WellKnownAlias, StringComparison.Ordinal)).ToArray();
        _copyableTypeSelectors = copyableTypeSelectors.Where(t => string.Equals(t.CopierName, WellKnownAlias, StringComparison.Ordinal)).ToArray();
        _options = options.Value;
    }

    /// <inheritdoc/>
    void IFieldCodec.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value)
    {
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
        {
            return;
        }

        // The schema type when serializing the field is the type of the codec.
        // In practice it could be any unique type as long as this codec is registered as the handler.
        // By checking against the codec type in IsSupportedType, the codec could also just be registered as an IGenericCodec.
        // Note that the codec is responsible for serializing the type of the value itself.
        writer.WriteFieldHeader(fieldIdDelta, expectedType, SelfType, WireType.TagDelimited);

        // Write the type name
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeaderExpected(0, WireType.LengthPrefixed);
        writer.Session.TypeCodec.WriteLengthPrefixed(ref writer, value.GetType());

        // Write the serialized payload
        // Note that the Utf8JsonWriter and PooledBuffer could be pooled as long as they're correctly
        // reset at the end of each use.
        var bufferWriter = new BufferWriterBox<PooledBuffer>(new PooledBuffer());
        try
        {
            var jsonWriter = new Utf8JsonWriter(bufferWriter, _options.WriterOptions);
            JsonSerializer.Serialize(jsonWriter, value, _options.SerializerOptions);
            jsonWriter.Flush();

            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeaderExpected(1, WireType.LengthPrefixed);
            writer.WriteVarUInt32((uint)bufferWriter.Value.Length);
            bufferWriter.Value.CopyTo(ref writer);
        }
        finally
        {
            bufferWriter.Value.Dispose();
        }

        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    object IFieldCodec.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.IsReference)
        {
            return ReferenceCodec.ReadReference(ref reader, field.FieldType);
        }

        field.EnsureWireTypeTagDelimited();

        var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
        object result = null;
        Type type = null;
        uint fieldId = 0;
        while (true)
        {
            var header = reader.ReadFieldHeader();
            if (header.IsEndBaseOrEndObject)
            {
                break;
            }

            fieldId += header.FieldIdDelta;
            switch (fieldId)
            {
                case 0:
                    ReferenceCodec.MarkValueField(reader.Session);
                    type = reader.Session.TypeCodec.ReadLengthPrefixed(ref reader);
                    break;
                case 1:
                    if (type is null)
                    {
                        ThrowTypeFieldMissing();
                    }

                    ReferenceCodec.MarkValueField(reader.Session);
                    var length = reader.ReadVarUInt32();

                    // To possibly improve efficiency, this could be converted to read a ReadOnlySequence<byte> instead of a byte array.
                    var tempBuffer = new PooledBuffer();
                    try
                    {
                        reader.ReadBytes(ref tempBuffer, (int)length);
                        var sequence = tempBuffer.AsReadOnlySequence();
                        var jsonReader = new Utf8JsonReader(sequence, _options.ReaderOptions);
                        if (typeof(JsonNode).IsAssignableFrom(type))
                        {
                            result = JsonNode.Parse(ref jsonReader);
                        }
                        else
                        {
                            result = JsonSerializer.Deserialize(ref jsonReader, type, _options.SerializerOptions);
                        }
                    }
                    finally
                    {
                        tempBuffer.Dispose();
                    }

                    break;
                default:
                    reader.ConsumeUnknownField(header);
                    break;
            }
        }

        ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
        return result;
    }

    /// <inheritdoc/>
    bool IGeneralizedCodec.IsSupportedType(Type type)
    {
        if (type == SelfType)
        {
            return true;
        }

        if (CommonCodecTypeFilter.IsAbstractOrFrameworkType(type))
        {
            return false;
        }

        if (IsNativelySupportedType(type))
        {
            return true;
        }

        foreach (var selector in _serializableTypeSelectors)
        {
            if (selector.IsSupportedType(type))
            {
                return true;
            }
        }

        if (_options.IsSerializableType?.Invoke(type) is bool value)
        {
            return value;
        }

        return false;
    }

    private static bool IsNativelySupportedType(Type type)
    {
        // Add types natively supported by System.Text.Json
        // From https://github.com/dotnet/runtime/blob/2c4d0df3b146f8322f676b83a53ca21a065bdfc7/src/libraries/System.Text.Json/gen/JsonSourceGenerator.Parser.cs#L1792-L1797
        return type == typeof(JsonElement)
            || type == typeof(JsonDocument)
            || type == typeof(JsonArray)
            || type == typeof(JsonObject)
            || type == typeof(JsonValue)
            || typeof(JsonNode).IsAssignableFrom(type);
    }

    /// <inheritdoc/>
    object IDeepCopier.DeepCopy(object input, CopyContext context)
    {
        if (context.TryGetCopy(input, out object result))
        {
            return result;
        }

        if (input is JsonNode jsonNode)
        {
            var copy = jsonNode.DeepClone();
            context.RecordCopy(input, copy);
            return copy;
        }

        var bufferWriter = new BufferWriterBox<PooledBuffer>(new PooledBuffer());
        try
        {
            var jsonWriter = new Utf8JsonWriter(bufferWriter, _options.WriterOptions);
            JsonSerializer.Serialize(jsonWriter, input, _options.SerializerOptions);
            jsonWriter.Flush();

            var sequence = bufferWriter.Value.AsReadOnlySequence();
            var jsonReader = new Utf8JsonReader(sequence, _options.ReaderOptions);
            result = JsonSerializer.Deserialize(ref jsonReader, input.GetType(), _options.SerializerOptions);
        }
        finally
        {
            bufferWriter.Value.Dispose();
        }

        context.RecordCopy(input, result);
        return result;
    }

    /// <inheritdoc/>
    bool IGeneralizedCopier.IsSupportedType(Type type)
    {
        if (CommonCodecTypeFilter.IsAbstractOrFrameworkType(type))
        {
            return false;
        }

        if (IsNativelySupportedType(type))
        {
            return true;
        }

        foreach (var selector in _copyableTypeSelectors)
        {
            if (selector.IsSupportedType(type))
            {
                return true;
            }
        }

        if (_options.IsCopyableType?.Invoke(type) is bool value)
        {
            return value;
        }

        return false;
    }

    /// <inheritdoc/>
    bool? ITypeFilter.IsTypeAllowed(Type type) => (((IGeneralizedCopier)this).IsSupportedType(type) || ((IGeneralizedCodec)this).IsSupportedType(type)) ? true : null;

    private static void ThrowTypeFieldMissing() => throw new RequiredFieldMissingException("Serialized value is missing its type field.");
}

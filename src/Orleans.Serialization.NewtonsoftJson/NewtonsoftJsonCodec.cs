using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization;

[Alias(WellKnownAlias)]
public class NewtonsoftJsonCodec : IGeneralizedCodec, IGeneralizedCopier, ITypeFilter
{
    private static readonly Type SelfType = typeof(NewtonsoftJsonCodec);
    private readonly NewtonsoftJsonCodecOptions _options;
    private readonly ICodecSelector[] _serializableTypeSelectors;
    private readonly ICopierSelector[] _copyableTypeSelectors;
    private readonly JsonSerializer _serializer;

    /// <summary>
    /// The well-known type alias for this codec.
    /// </summary>
    public const string WellKnownAlias = "json.net";

    /// <summary>
    /// Initializes a new instance of the <see cref="NewtonsoftJsonCodec"/> class.
    /// </summary>
    /// <param name="serializableTypeSelectors">Filters used to indicate which types should be serialized by this codec.</param>
    /// <param name="copyableTypeSelectors">Filters used to indicate which types should be copied by this codec.</param>
    /// <param name="options">The JSON codec options.</param>
    public NewtonsoftJsonCodec(
        IEnumerable<ICodecSelector> serializableTypeSelectors,
        IEnumerable<ICopierSelector> copyableTypeSelectors,
        IOptions<NewtonsoftJsonCodecOptions> options)
    {
        _options = options.Value;
        _serializableTypeSelectors = serializableTypeSelectors.Where(t => string.Equals(t.CodecName, WellKnownAlias, StringComparison.Ordinal)).ToArray();
        _copyableTypeSelectors = copyableTypeSelectors.Where(t => string.Equals(t.CopierName, WellKnownAlias, StringComparison.Ordinal)).ToArray();
        _serializer = JsonSerializer.Create(_options.SerializerSettings);
    }

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
        var serializedValue = JsonConvert.SerializeObject(value, _options.SerializerSettings);
        StringCodec.WriteField(ref writer, 1, serializedValue);

        writer.WriteEndObject();
    }

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

                    // To possibly improve efficiency, this could be converted to read a ReadOnlySequence<byte> instead of a byte array.
                    var serializedValue = StringCodec.ReadValue(ref reader, header);
                    result = JsonConvert.DeserializeObject(serializedValue, type, _options.SerializerSettings);
                    ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
                    break;
                default:
                    reader.ConsumeUnknownField(header);
                    break;
            }
        }

        return result;
    }

    bool IGeneralizedCodec.IsSupportedType(Type type)
    {
        if (type == SelfType)
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

    /// <inheritdoc/>
    object IDeepCopier.DeepCopy(object input, CopyContext context)
    {
        if (context.TryGetCopy(input, out object result))
            return result;

        var stream = PooledBufferStream.Rent();
        try
        {
            var type = input.GetType();
            var streamWriter = new StreamWriter(stream);
            using (var textWriter = new JsonTextWriter(streamWriter) { CloseOutput = false })
                _serializer.Serialize(textWriter, input, type);
            streamWriter.Flush();

            stream.Position = 0;

            var streamReader = new StreamReader(stream);
            using var jsonReader = new JsonTextReader(streamReader);
            result = _serializer.Deserialize(jsonReader, type);
        }
        finally
        {
            PooledBufferStream.Return(stream);
        }

        context.RecordCopy(input, result);
        return result;
    }

    /// <inheritdoc/>
    bool IGeneralizedCopier.IsSupportedType(Type type)
    {
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

    private static void ThrowTypeFieldMissing() => throw new RequiredFieldMissingException("Serialized value is missing its type field.");

    private bool IsSupportedType(Type type) => ((IGeneralizedCodec)this).IsSupportedType(type) || ((IGeneralizedCopier)this).IsSupportedType(type);
    bool? ITypeFilter.IsTypeAllowed(Type type) => IsSupportedType(type) ? true : null;
}
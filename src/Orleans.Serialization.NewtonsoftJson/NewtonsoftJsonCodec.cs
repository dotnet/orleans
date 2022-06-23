using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;
using Newtonsoft.Json;
using System;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Buffers.Adaptors;
using System.IO;
using Microsoft.Extensions.Options;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Serialization;

[WellKnownAlias(WellKnownAlias)]
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

    void IFieldCodec<object>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value)
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

        var type = value.GetType();

        // Write the type name
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(0, typeof(byte[]), typeof(byte[]), WireType.LengthPrefixed);
        writer.Session.TypeCodec.WriteLengthPrefixed(ref writer, type);

        // Write the serialized payload
        var serializedValue = JsonConvert.SerializeObject(value, _options.SerializerSettings);
        StringCodec.WriteField(ref writer, 1, typeof(string), serializedValue);

        writer.WriteEndObject();
    }

    object IFieldCodec<object>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.WireType == WireType.Reference)
        {
            return ReferenceCodec.ReadReference<object, TInput>(ref reader, field);
        }

        if (field.WireType != WireType.TagDelimited)
        {
            ThrowUnsupportedWireTypeException(field);
        }

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
    object IDeepCopier<object>.DeepCopy(object input, CopyContext context)
    {
        if (input is null) return null;

        var stream = PooledBufferStream.Rent();
        try
        {
            var type = input.GetType();
            using var streamWriter = new StreamWriter(stream);
            using var textWriter = new JsonTextWriter(streamWriter);
            _serializer.Serialize(textWriter, input, type);
            textWriter.Flush();

            stream.Position = 0;

            using var streamReader = new StreamReader(stream);
            using var jsonReader = new JsonTextReader(streamReader);
            var result = _serializer.Deserialize(jsonReader, type);

            context.RecordCopy(input, result);
            return result;
        }
        finally
        {
            PooledBufferStream.Return(stream);
        }
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
        $"Only a {nameof(WireType)} value of {WireType.TagDelimited} is supported for JSON fields. {field}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTypeFieldMissing() => throw new RequiredFieldMissingException("Serialized value is missing its type field.");

    private bool IsSupportedType(Type type) => ((IGeneralizedCodec)this).IsSupportedType(type) || ((IGeneralizedCopier)this).IsSupportedType(type);
    bool? ITypeFilter.IsTypeAllowed(Type type) => IsSupportedType(type) ? true : null;
}
using Google.Protobuf;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Orleans.Serialization;

[Alias(WellKnownAlias)]
public sealed class ProtobufCodec : IGeneralizedCodec, IGeneralizedCopier, ITypeFilter
{
    public const string WellKnownAlias = "protobuf";

    private static readonly ConcurrentDictionary<RuntimeTypeHandle, MessageParser> Parsers = new();

    private readonly ICodecSelector[] _serializableTypeSelectors;
    private readonly ICopierSelector[] _copyableTypeSelectors;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtobufCodec"/> class.
    /// </summary>
    /// <param name="serializableTypeSelectors">Filters used to indicate which types should be serialized by this codec.</param>
    /// <param name="copyableTypeSelectors">Filters used to indicate which types should be copied by this codec.</param>
    public ProtobufCodec(
        IEnumerable<ICodecSelector> serializableTypeSelectors,
        IEnumerable<ICopierSelector> copyableTypeSelectors)
    {
        _serializableTypeSelectors = serializableTypeSelectors.Where(t => string.Equals(t.CodecName, WellKnownAlias, StringComparison.Ordinal)).ToArray();
        _copyableTypeSelectors = copyableTypeSelectors.Where(t => string.Equals(t.CopierName, WellKnownAlias, StringComparison.Ordinal)).ToArray();
    }

    public object DeepCopy(object input, CopyContext context)
    {
        if (!context.TryGetCopy(input, out object result))
        {
            dynamic dynamicSource = input;
            result = dynamicSource.Clone();

            context.RecordCopy(input, result);
        }

        return result;
    }

    /// <inheritdoc/>
    bool IGeneralizedCodec.IsSupportedType(Type type)
    {
        foreach (var selector in _serializableTypeSelectors)
        {
            if (selector.IsSupportedType(type))
            {
                return IsMessageParser(type);
            }
        }

        return false;
    }

    /// <inheritdoc/>
    bool IGeneralizedCopier.IsSupportedType(Type type)
    {
        foreach (var selector in _copyableTypeSelectors)
        {
            if (selector.IsSupportedType(type))
            {
                return IsMessageParser(type);
            }
        }

        return false;
    }

    /// <inheritdoc/>
    bool? ITypeFilter.IsTypeAllowed(Type type)
    {
        if (!typeof(IMessage).IsAssignableFrom(type))
        {
            return null;
        }

        return ((IGeneralizedCodec)this).IsSupportedType(type) || ((IGeneralizedCopier)this).IsSupportedType(type);
    } 

    static bool IsMessageParser(Type type)
    {
        if (!Parsers.ContainsKey(type.TypeHandle))
        {
            var prop = type.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static);
            if (prop is null)
            {
                return false;
            }

            if (prop.GetValue(null, null) is not MessageParser parser)
            {
                throw new ArgumentNullException(nameof(parser));
            }

            Parsers.TryAdd(type.TypeHandle, parser);
        }

        return true;
    }

    /// <inheritdoc/>
    object IFieldCodec.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.IsReference)
        {
            return ReferenceCodec.ReadReference(ref reader, field.FieldType);
        }

        field.EnsureWireTypeTagDelimited();

        if (!Parsers.TryGetValue(field.FieldType.TypeHandle, out var parser))
        {
            throw new ArgumentException($"No parser found for the expected type {field.FieldType}", nameof(TInput));
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

                    ReferenceCodec.MarkValueField(reader.Session);
                    var length = (int)reader.ReadVarUInt32();

                    using (var buffer = new PooledArrayBufferWriter())
                    {
                        var spanBuffer = buffer.GetSpan(length)[..length];
                        reader.ReadBytes(spanBuffer);
                        result = parser.ParseFrom(spanBuffer);
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

    private static void ThrowTypeFieldMissing() => throw new RequiredFieldMissingException("Serialized value is missing its type field.");

    /// <inheritdoc/>
    void IFieldCodec.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value)
    {
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
        {
            return;
        }

        if (value is not IMessage protobufMessage)
        {
            throw new ArgumentException("The provided value for serialization in not an instance of IMessage");
        }

        writer.WriteFieldHeader(fieldIdDelta, expectedType, protobufMessage.GetType(), WireType.TagDelimited);

        // Write the type name
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeaderExpected(0, WireType.LengthPrefixed);
        writer.Session.TypeCodec.WriteLengthPrefixed(ref writer, value.GetType());

        var messageSize = protobufMessage.CalculateSize();

        using var buffer = new PooledArrayBufferWriter();
        var spanBuffer = buffer.GetSpan(messageSize)[..messageSize];

        // Write the serialized payload
        protobufMessage.WriteTo(spanBuffer);

        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeaderExpected(1, WireType.LengthPrefixed);
        writer.WriteVarUInt32((uint)spanBuffer.Length);
        writer.Write(spanBuffer);

        writer.WriteEndObject();
    }
}

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using MessagePack;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization;

/// <summary>
/// A serialization codec which uses <see cref="MessagePackSerializer"/>.
/// </summary>
/// <remarks>
/// MessagePack codec performs slightly worse than default Orleans serializer, if performance is critical for your application, consider using default serialization.
/// </remarks>
[Alias(WellKnownAlias)]
public class MessagePackCodec : IGeneralizedCodec, IGeneralizedCopier, ITypeFilter
{
    private static readonly ConcurrentDictionary<Type, bool> SupportedTypes = new();

    private static readonly Type SelfType = typeof(MessagePackCodec);

    private readonly ICodecSelector[] _serializableTypeSelectors;
    private readonly ICopierSelector[] _copyableTypeSelectors;
    private readonly MessagePackCodecOptions _options;

    /// <summary>
    /// The well-known type alias for this codec.
    /// </summary>
    public const string WellKnownAlias = "msgpack";

    /// <summary>
    /// Initializes a new instance of the <see cref="MessagePackCodec"/> class.
    /// </summary>
    /// /// <param name="serializableTypeSelectors">Filters used to indicate which types should be serialized by this codec.</param>
    /// <param name="copyableTypeSelectors">Filters used to indicate which types should be copied by this codec.</param>
    /// <param name="options">The MessagePack codec options.</param>
    public MessagePackCodec(
        IEnumerable<ICodecSelector> serializableTypeSelectors,
        IEnumerable<ICopierSelector> copyableTypeSelectors,
        IOptions<MessagePackCodecOptions> options)
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
        writer.WriteFieldHeader(fieldIdDelta, expectedType, SelfType, WireType.TagDelimited);

        // Write the type name
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeaderExpected(0, WireType.LengthPrefixed);
        writer.Session.TypeCodec.WriteLengthPrefixed(ref writer, value.GetType());

        var bufferWriter = new BufferWriterBox<PooledBuffer>(new());
        try
        {

            var msgPackWriter = new MessagePackWriter(bufferWriter);
            MessagePackSerializer.Serialize(value.GetType(), ref msgPackWriter, value, _options.SerializerOptions);
            msgPackWriter.Flush();

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

                    var bufferWriter = new BufferWriterBox<PooledBuffer>(new());
                    try
                    {
                        reader.ReadBytes(ref bufferWriter, (int)length);
                        result = MessagePackSerializer.Deserialize(type, bufferWriter.Value.AsReadOnlySequence(), _options.SerializerOptions);
                    }
                    finally
                    {
                        bufferWriter.Value.Dispose();
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

        return IsMessagePackContract(type, _options.AllowDataContractAttributes);
    }

    /// <inheritdoc/>
    object IDeepCopier.DeepCopy(object input, CopyContext context)
    {
        if (context.TryGetCopy(input, out object result))
        {
            return result;
        }

        var bufferWriter = new BufferWriterBox<PooledBuffer>(new());
        try
        {
            var msgPackWriter = new MessagePackWriter(bufferWriter);
            MessagePackSerializer.Serialize(input.GetType(), ref msgPackWriter, input, _options.SerializerOptions);
            msgPackWriter.Flush();

            var sequence = bufferWriter.Value.AsReadOnlySequence();
            result = MessagePackSerializer.Deserialize(input.GetType(), sequence, _options.SerializerOptions);
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

        return IsMessagePackContract(type, _options.AllowDataContractAttributes);
    }

    /// <inheritdoc/>
    bool? ITypeFilter.IsTypeAllowed(Type type) => (((IGeneralizedCopier)this).IsSupportedType(type) || ((IGeneralizedCodec)this).IsSupportedType(type)) ? true : null;

    private static bool IsMessagePackContract(Type type, bool allowDataContractAttribute)
    {
        if (SupportedTypes.TryGetValue(type, out bool isMsgPackContract))
        {
            return isMsgPackContract;
        }

        isMsgPackContract = type.GetCustomAttribute<MessagePackObjectAttribute>() is not null;

        if (!isMsgPackContract && allowDataContractAttribute)
        {
            isMsgPackContract = type.GetCustomAttribute<DataContractAttribute>() is DataContractAttribute;
        }

        SupportedTypes.TryAdd(type, isMsgPackContract);
        return isMsgPackContract;
    }

    private static void ThrowTypeFieldMissing() => throw new RequiredFieldMissingException("Serialized value is missing its type field.");
}
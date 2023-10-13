using Google.Protobuf;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Serialization;

[Alias(WellKnownAlias)]
public sealed class ProtobufCodec : IGeneralizedCodec, IGeneralizedCopier, ITypeFilter
{
    public const string WellKnownAlias = "protobuf";

    private static readonly Type SelfType = typeof(ProtobufCodec);
    private static readonly Type MessageType = typeof(IMessage);
    private static readonly Type MessageGenericType = typeof(IMessage<>);
    private static readonly ConcurrentDictionary<RuntimeTypeHandle, MessageParser> MessageParsers = new();

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

    /// <inheritdoc/>
    public object DeepCopy(object input, CopyContext context)
    {
        if (!context.TryGetCopy(input, out object result))
        {
            if (input is not IMessage protobufMessage)
            {
                throw new InvalidOperationException("Input is not a protobuf message");
            }

            var messageSize = protobufMessage.CalculateSize();
            using var buffer = new PooledBuffer();
            var spanBuffer = buffer.GetSpan(messageSize)[..messageSize];
            protobufMessage.WriteTo(spanBuffer);

            result = protobufMessage.Descriptor.Parser.ParseFrom(spanBuffer);

            context.RecordCopy(input, result);
        }

        return result;
    }

    /// <inheritdoc/>
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
                return IsProtobufMessage(type);
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
                return IsProtobufMessage(type);
            }
        }

        return false;
    }

    /// <inheritdoc/>
    bool? ITypeFilter.IsTypeAllowed(Type type)
    {
        if (!MessageType.IsAssignableFrom(type))
        {
            return null;
        }

        if (type == MessageType)
        {
            // While IMessage is the basis of all supported types, it isn't directly supported
            return null;
        }

        return ((IGeneralizedCodec)this).IsSupportedType(type) || ((IGeneralizedCopier)this).IsSupportedType(type);
    }

    private static bool IsProtobufMessage(Type type)
    {
        if (type == MessageType)
        {
            // Not a concrete implementation, so not directly serializable
            return false;
        }

        if (type == MessageGenericType)
        {
            // Not a concrete implementation, but the generic type does give the concrete type
            type = type.GenericTypeArguments[0];
        }

        if (!MessageParsers.ContainsKey(type.TypeHandle))
        {
            if (Activator.CreateInstance(type) is not IMessage protobufMessageInstance)
            {
                return false;
            }

            MessageParsers.TryAdd(type.TypeHandle, protobufMessageInstance.Descriptor.Parser);
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

                    if (!MessageParsers.TryGetValue(type.TypeHandle, out var messageParser))
                    {
                        throw new ArgumentException($"No parser found for the expected type {type.Name}", nameof(TInput));
                    }

                    ReferenceCodec.MarkValueField(reader.Session);
                    var length = (int)reader.ReadVarUInt32();

                    using (var buffer = new PooledBuffer())
                    {
                        var spanBuffer = buffer.GetSpan(length)[..length];
                        reader.ReadBytes(spanBuffer);
                        result = messageParser.ParseFrom(spanBuffer);
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

        writer.WriteFieldHeader(fieldIdDelta, expectedType, SelfType, WireType.TagDelimited);

        // Write the type name
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeaderExpected(0, WireType.LengthPrefixed);
        writer.Session.TypeCodec.WriteLengthPrefixed(ref writer, value.GetType());

        var messageSize = protobufMessage.CalculateSize();

        using var buffer = new PooledBuffer();
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

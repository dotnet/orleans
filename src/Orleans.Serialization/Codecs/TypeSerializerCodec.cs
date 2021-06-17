using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class TypeSerializerCodec : IFieldCodec<Type>, IDerivedTypeCodec
    {
        private static readonly Type SchemaTypeType = typeof(SchemaType);
        private static readonly Type TypeType = typeof(Type);
        private static readonly Type ByteArrayType = typeof(byte[]);
        private static readonly Type UIntType = typeof(uint);

        void IFieldCodec<Type>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Type value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Type value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, TypeType, WireType.TagDelimited);
            var (schemaType, id) = GetSchemaType(writer.Session, value);

            // Write the encoding type.
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(0, SchemaTypeType, SchemaTypeType, WireType.VarInt);
            writer.WriteVarUInt32((uint)schemaType);

            if (schemaType == SchemaType.Encoded)
            {
                // If the type is encoded, write the length-prefixed bytes.
                ReferenceCodec.MarkValueField(writer.Session);
                writer.WriteFieldHeader(1, ByteArrayType, ByteArrayType, WireType.LengthPrefixed);
                writer.Session.TypeCodec.WriteLengthPrefixed(ref writer, value);
            }
            else
            {
                // If the type is referenced or well-known, write it as a varint.
                ReferenceCodec.MarkValueField(writer.Session);
                writer.WriteFieldHeader(2, UIntType, UIntType, WireType.VarInt);
                writer.WriteVarUInt32((uint)id);
            }

            writer.WriteEndObject();
        }

        Type IFieldCodec<Type>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        public static Type ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<Type, TInput>(ref reader, field);
            }

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            uint fieldId = 0;
            var schemaType = default(SchemaType);
            uint id = 0;
            Type result = null;
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject)
                {
                    break;
                }

                ReferenceCodec.MarkValueField(reader.Session);
                fieldId += header.FieldIdDelta;
                switch (fieldId)
                {
                    case 0:
                        schemaType = (SchemaType)reader.ReadVarUInt32();
                        break;
                    case 1:
                        result = reader.Session.TypeCodec.ReadLengthPrefixed(ref reader);
                        break;
                    case 2:
                        id = reader.ReadVarUInt32();
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            switch (schemaType)
            {
                case SchemaType.Referenced:
                    if (reader.Session.ReferencedTypes.TryGetReferencedType(id, out result))
                    {
                        break;
                    }

                    return ThrowUnknownReferencedType(id);
                case SchemaType.WellKnown:
                    if (reader.Session.WellKnownTypes.TryGetWellKnownType(id, out result))
                    {
                        break;
                    }

                    return ThrowUnknownWellKnownType(id);
                case SchemaType.Encoded:
                    if (result is not null)
                    {
                        break;
                    }

                    return ThrowMissingType();
                default:
                    return ThrowInvalidSchemaType(schemaType);
            }

            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }

        private static (SchemaType, uint) GetSchemaType(SerializerSession session, Type actualType)
        {
            if (session.WellKnownTypes.TryGetWellKnownTypeId(actualType, out uint typeId))
            {
                return (SchemaType.WellKnown, typeId);
            }

            if (session.ReferencedTypes.TryGetTypeReference(actualType, out uint reference))
            {
                return (SchemaType.Referenced, reference);
            }

            return (SchemaType.Encoded, 0);
        }

        private static Type ThrowInvalidSchemaType(SchemaType schemaType) => throw new NotSupportedException(
            $"SchemaType {schemaType} is not supported by {nameof(TypeSerializerCodec)}.");

        private static Type ThrowUnknownReferencedType(uint id) => throw new UnknownReferencedTypeException(id);
        private static Type ThrowUnknownWellKnownType(uint id) => throw new UnknownWellKnownTypeException(id);
        private static Type ThrowMissingType() => throw new TypeMissingException();
    }

    [RegisterCopier]
    public sealed class TypeCopier : IDeepCopier<Type>, IDerivedTypeCopier
    {
        public Type DeepCopy(Type input, CopyContext context) => input;
    }
}
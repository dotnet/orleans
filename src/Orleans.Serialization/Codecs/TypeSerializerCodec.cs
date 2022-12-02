using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serialzier for <see cref="Type"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class TypeSerializerCodec : IFieldCodec<Type>, IDerivedTypeCodec
    {
        void IFieldCodec<Type>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Type value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, typeof(Type), value))
            {
                return;
            }

            writer.WriteStartObject(fieldIdDelta, expectedType, typeof(Type));
            WriteField(ref writer, value);
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceFieldExpected(ref writer, fieldIdDelta, value))
            {
                return;
            }

            writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.TagDelimited);
            WriteField(ref writer, value);
        }

        private static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, Type value) where TBufferWriter : IBufferWriter<byte>
        {
            var schemaType = writer.Session.WellKnownTypes.TryGetWellKnownTypeId(value, out var id) ? SchemaType.WellKnown
                : writer.Session.ReferencedTypes.TryGetTypeReference(value, out id) ? SchemaType.Referenced
                : SchemaType.Encoded;

            // Write the encoding type.
            ByteCodec.WriteField(ref writer, 0, (byte)schemaType);

            if (schemaType == SchemaType.Encoded)
            {
                // If the type is encoded, write the length-prefixed bytes.
                ReferenceCodec.MarkValueField(writer.Session);
                writer.WriteFieldHeaderExpected(1, WireType.LengthPrefixed);
                writer.Session.TypeCodec.WriteLengthPrefixed(ref writer, value);
            }
            else
            {
                // If the type is referenced or well-known, write it as a varint.
                UInt32Codec.WriteField(ref writer, 2, id);
            }

            writer.WriteEndObject();
        }

        /// <inheritdoc />
        Type IFieldCodec<Type>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        public static Type ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<Type, TInput>(ref reader, field);
            }

            field.EnsureWireTypeTagDelimited();

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            uint fieldId = 0;
            var schemaType = default(SchemaType);
            uint id = 0;
            Type result = null;
            while (true)
            {
                reader.ReadFieldHeader(ref field);
                if (field.IsEndBaseOrEndObject)
                {
                    break;
                }

                fieldId += field.FieldIdDelta;
                switch (fieldId)
                {
                    case 0:
                        schemaType = (SchemaType)ByteCodec.ReadValue(ref reader, field);
                        break;
                    case 1:
                        ReferenceCodec.MarkValueField(reader.Session);
                        result = reader.Session.TypeCodec.ReadLengthPrefixed(ref reader);
                        break;
                    case 2:
                        id = UInt32Codec.ReadValue(ref reader, field);
                        break;
                    default:
                        reader.ConsumeUnknownField(field);
                        break;
                }
            }

            switch (schemaType)
            {
                case SchemaType.Referenced:
                    result = reader.Session.ReferencedTypes.GetReferencedType(id);
                    break;

                case SchemaType.WellKnown:
                    if (!reader.Session.WellKnownTypes.TryGetWellKnownType(id, out result))
                        ThrowUnknownWellKnownType(id);
                    break;

                case SchemaType.Encoded:
                    // Type codec should not update the type reference map, otherwise unknown-field deserialization could be broken
                    break;

                default:
                    ThrowInvalidSchemaType(schemaType);
                    break;
            }

            if (result is null) ThrowMissingType();
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }

        private static void ThrowInvalidSchemaType(SchemaType schemaType) => throw new NotSupportedException(
            $"SchemaType {schemaType} is not supported by {nameof(TypeSerializerCodec)}.");

        private static void ThrowUnknownWellKnownType(uint id) => throw new UnknownWellKnownTypeException(id);
        private static void ThrowMissingType() => throw new TypeMissingException();
    }
}
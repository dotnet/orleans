using Orleans.Serialization.Buffers;
using Orleans.Serialization.TypeSystem;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Codec for operating with the wire format.
    /// </summary>
    public static class FieldHeaderCodec
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteFieldHeader<TBufferWriter>(
            ref this Writer<TBufferWriter> writer,
            uint fieldId,
            Type expectedType,
            Type actualType,
            WireType wireType) where TBufferWriter : IBufferWriter<byte>
        {
            var hasExtendedFieldId = fieldId > Tag.MaxEmbeddedFieldIdDelta;
            var embeddedFieldId = hasExtendedFieldId ? Tag.FieldIdCompleteMask : (byte)fieldId;
            var tag = (byte)((byte)wireType | embeddedFieldId);

            if (actualType == expectedType)
            {
                writer.WriteByte((byte)(tag | (byte)SchemaType.Expected));
                if (hasExtendedFieldId)
                {
                    writer.WriteVarUInt32(fieldId);
                }
            }
            else if (writer.Session.WellKnownTypes.TryGetWellKnownTypeId(actualType, out var typeOrReferenceId))
            {
                writer.WriteByte((byte)(tag | (byte)SchemaType.WellKnown));
                if (hasExtendedFieldId)
                {
                    writer.WriteVarUInt32(fieldId);
                }

                writer.WriteVarUInt32(typeOrReferenceId);
            }
            else if (writer.Session.ReferencedTypes.TryGetTypeReference(actualType, out typeOrReferenceId))
            {
                writer.WriteByte((byte)(tag | (byte)SchemaType.Referenced));
                if (hasExtendedFieldId)
                {
                    writer.WriteVarUInt32(fieldId);
                }

                writer.WriteVarUInt32(typeOrReferenceId);
            }
            else
            {
                writer.WriteByte((byte)(tag | (byte)SchemaType.Encoded));
                if (hasExtendedFieldId)
                {
                    writer.WriteVarUInt32(fieldId);
                }

                writer.Session.TypeCodec.WriteEncodedType(ref writer, actualType);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteFieldHeaderExpected<TBufferWriter>(this ref Writer<TBufferWriter> writer, uint fieldId, WireType wireType)
            where TBufferWriter : IBufferWriter<byte>
        {
            if (fieldId < Tag.MaxEmbeddedFieldIdDelta)
            {
                WriteFieldHeaderExpectedEmbedded(ref writer, fieldId, wireType);
            }
            else
            {
                WriteFieldHeaderExpectedExtended(ref writer, fieldId, wireType);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteFieldHeaderExpectedEmbedded<TBufferWriter>(this ref Writer<TBufferWriter> writer, uint fieldId, WireType wireType)
            where TBufferWriter : IBufferWriter<byte> => writer.WriteByte((byte)((byte)wireType | (byte)fieldId));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteFieldHeaderExpectedExtended<TBufferWriter>(this ref Writer<TBufferWriter> writer, uint fieldId, WireType wireType)
            where TBufferWriter : IBufferWriter<byte>
        {
            writer.WriteByte((byte)((byte)wireType | Tag.FieldIdCompleteMask));
            writer.WriteVarUInt32(fieldId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadFieldHeader<TInput>(ref this Reader<TInput> reader, ref Field field)
        {
            var tag = reader.ReadByte();

            if (tag != (byte)WireType.Extended && ((tag & Tag.FieldIdCompleteMask) == Tag.FieldIdCompleteMask || (tag & Tag.SchemaTypeMask) != (byte)SchemaType.Expected))
            {
                field.Tag = tag;
                ReadExtendedFieldHeader(ref reader, ref field);
            }
            else
            {
                field.Tag = tag;
                field.FieldIdDeltaRaw = default;
                field.FieldTypeRaw = default;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Field ReadFieldHeader<TInput>(ref this Reader<TInput> reader)
        {
            Field field = default;
            var tag = reader.ReadByte();
            if (tag != (byte)WireType.Extended && ((tag & Tag.FieldIdCompleteMask) == Tag.FieldIdCompleteMask || (tag & Tag.SchemaTypeMask) != (byte)SchemaType.Expected))
            {
                field.Tag = tag;
                ReadExtendedFieldHeader(ref reader, ref field);
            }
            else
            {
                field.Tag = tag;
                field.FieldIdDeltaRaw = default;
                field.FieldTypeRaw = default;
            }

            return field;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReadExtendedFieldHeader<TInput>(ref this Reader<TInput> reader, ref Field field)
        {
            // If all of the field id delta bits are set and the field isn't an extended wiretype field, read the extended field id delta
            var notExtended = (field.Tag & (byte)WireType.Extended) != (byte)WireType.Extended;
            if ((field.Tag & Tag.FieldIdCompleteMask) == Tag.FieldIdCompleteMask && notExtended)
            {
                field.FieldIdDeltaRaw = reader.ReadVarUInt32NoInlining();
            }
            else
            {
                field.FieldIdDeltaRaw = 0;
            }

            // If schema type is valid, read the type.
            var schemaType = (SchemaType)(field.Tag & Tag.SchemaTypeMask);
            if (notExtended && schemaType != SchemaType.Expected)
            {
                field.FieldTypeRaw = reader.ReadType(schemaType);
            }
            else
            {
                field.FieldTypeRaw = default;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Type ReadType<TInput>(this ref Reader<TInput> reader, SchemaType schemaType)
        {
            switch (schemaType)
            {
                case SchemaType.Expected:
                    return null;
                case SchemaType.WellKnown:
                    var typeId = reader.ReadVarUInt32();
                    return reader.Session.WellKnownTypes.GetWellKnownType(typeId);
                case SchemaType.Encoded:
                    _ = reader.Session.TypeCodec.TryRead(ref reader, out Type encoded);
                    return encoded;
                case SchemaType.Referenced:
                    var reference = reader.ReadVarUInt32();
                    return reader.Session.ReferencedTypes.GetReferencedType(reference);
                default:
                    return ExceptionHelper.ThrowArgumentOutOfRange<Type>(nameof(SchemaType));
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (Type type, string typeName) ReadTypeForAnalysis<TInput>(this ref Reader<TInput> reader, SchemaType schemaType)
        {
            switch (schemaType)
            {
                case SchemaType.Expected:
                    return (null, "Expected");
                case SchemaType.WellKnown:
                    { 
                        var typeId = reader.ReadVarUInt32();
                        if (reader.Session.WellKnownTypes.TryGetWellKnownType(typeId, out var type))
                        {
                            return (type, $"WellKnown {typeId} ({(type is null ? "null" : RuntimeTypeNameFormatter.Format(type))})");
                        }
                        else
                        {
                            return (null, $"WellKnown {typeId} (unknown)");
                        }
                    }
                case SchemaType.Encoded:
                    {
                        var found = reader.Session.TypeCodec.TryReadForAnalysis(ref reader, out Type encoded, out var typeString);
                        return (encoded, $"Encoded \"{typeString}\" ({(found ? RuntimeTypeNameFormatter.Format(encoded) : "not found")})");
                    }
                case SchemaType.Referenced:
                    {
                        var reference = reader.ReadVarUInt32();
                        var found = reader.Session.ReferencedTypes.TryGetReferencedType(reference, out var type);
                        return (type, $"Referenced {reference} ({(found ? RuntimeTypeNameFormatter.Format(type) : "not found")})");
                    }
                default:
                    throw new ArgumentOutOfRangeException(nameof(schemaType));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (Field Field, string Type) ReadFieldHeaderForAnalysis<TInput>(ref this Reader<TInput> reader)
        {
            Field field = default;
            string type = default;
            var tag = reader.ReadByte();
            if (tag != (byte)WireType.Extended && ((tag & Tag.FieldIdCompleteMask) == Tag.FieldIdCompleteMask || (tag & Tag.SchemaTypeMask) != (byte)SchemaType.Expected))
            {
                field.Tag = tag;
                ReadFieldHeaderForAnalysisSlow(ref reader, ref field, ref type);
            }
            else
            {
                field.Tag = tag;
                field.FieldIdDeltaRaw = default;
                field.FieldTypeRaw = default;
                type = "Expected";
            }

            return (field, type);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadFieldHeaderForAnalysisSlow<TInput>(ref this Reader<TInput> reader, ref Field field, ref string type)
        {
            var notExtended = (field.Tag & (byte)WireType.Extended) != (byte)WireType.Extended;
            if ((field.Tag & Tag.FieldIdCompleteMask) == Tag.FieldIdCompleteMask && notExtended)
            {
                field.FieldIdDeltaRaw = reader.ReadVarUInt32NoInlining();
            }
            else
            {
                field.FieldIdDeltaRaw = 0;
            }

            // If schema type is valid, read the type.
            var schemaType = (SchemaType)(field.Tag & Tag.SchemaTypeMask);
            if (notExtended && schemaType != SchemaType.Expected)
            {
                (field.FieldTypeRaw, type) = reader.ReadTypeForAnalysis(schemaType);
            }
            else
            {
                field.FieldTypeRaw = default;
            }
        }

    }
}
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
        /// <summary>
        /// Writes the field header.
        /// </summary>
        /// <typeparam name="TBufferWriter">The underlying buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldId">The field identifier.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="actualType">The actual type.</param>
        /// <param name="wireType">The wire type.</param>
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

            if (actualType is null || actualType == expectedType)
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
            else if (writer.Session.ReferencedTypes.GetOrAddTypeReference(actualType, out typeOrReferenceId))
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

        /// <summary>
        /// Writes an expected field header value.
        /// </summary>
        /// <typeparam name="TBufferWriter">The underlying buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldId">The field identifier.</param>
        /// <param name="wireType">The wire type of the field.</param>
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

        /// <summary>
        /// Writes an field header value with an expected type and an embedded field identifier.
        /// </summary>
        /// <typeparam name="TBufferWriter">The underlying buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldId">The field identifier.</param>
        /// <param name="wireType">The wire type.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteFieldHeaderExpectedEmbedded<TBufferWriter>(this ref Writer<TBufferWriter> writer, uint fieldId, WireType wireType)
                    where TBufferWriter : IBufferWriter<byte> => writer.WriteByte((byte)((byte)wireType | (byte)fieldId));

        /// <summary>
        /// Writes a field header with an expected type and an extended field id.
        /// </summary>
        /// <typeparam name="TBufferWriter">The underlying buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldId">The field identifier.</param>
        /// <param name="wireType">The wire type.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteFieldHeaderExpectedExtended<TBufferWriter>(this ref Writer<TBufferWriter> writer, uint fieldId, WireType wireType)
                    where TBufferWriter : IBufferWriter<byte>
        {
            writer.WriteByte((byte)((byte)wireType | Tag.FieldIdCompleteMask));
            writer.WriteVarUInt32(fieldId);
        }

        /// <summary>
        /// Reads a field header.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field header.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadFieldHeader<TInput>(ref this Reader<TInput> reader, scoped ref Field field)
        {
            var tag = (uint)reader.ReadByte();
            field.Tag = new(tag);
            // If the id or schema type are required and were not encoded into the tag, read the extended header data.
            if (tag < (byte)WireType.Extended && (tag & (Tag.FieldIdCompleteMask | Tag.SchemaTypeMask)) >= Tag.FieldIdCompleteMask)
            {
                ReadExtendedFieldHeader(ref reader, ref field);
            }
            else
            {
                field.FieldIdDeltaRaw = tag & Tag.FieldIdMask;
                field.FieldTypeRaw = default;
            }
        }

        /// <summary>
        /// Reads a field header.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <returns>The field header.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Field ReadFieldHeader<TInput>(ref this Reader<TInput> reader)
        {
            Unsafe.SkipInit(out Field field);
            var tag = (uint)reader.ReadByte();
            field.Tag = new(tag);
            // If the id or schema type are required and were not encoded into the tag, read the extended header data.
            if (tag < (byte)WireType.Extended && (tag & (Tag.FieldIdCompleteMask | Tag.SchemaTypeMask)) >= Tag.FieldIdCompleteMask)
            {
                ReadExtendedFieldHeader(ref reader, ref field);
            }
            else
            {
                field.FieldIdDeltaRaw = tag & Tag.FieldIdMask;
                field.FieldTypeRaw = default;
            }

            return field;
        }

        /// <summary>
        /// Reads an extended field header.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadExtendedFieldHeader<TInput>(ref this Reader<TInput> reader, scoped ref Field field)
        {
            var fieldId = field.Tag.FieldIdDelta;
            if (fieldId == Tag.FieldIdCompleteMask)
            {
                field.FieldIdDeltaRaw = reader.ReadVarUInt32NoInlining();
            }
            else
            {
                field.FieldIdDeltaRaw = fieldId;
            }

            // If schema type is valid, read the type.
            var schemaType = field.Tag.SchemaType;
            if (schemaType != SchemaType.Expected)
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
                case SchemaType.WellKnown:
                    var typeId = reader.ReadVarUInt32();
                    return reader.Session.WellKnownTypes.GetWellKnownType(typeId);
                case SchemaType.Encoded:
                    var encoded = reader.Session.TypeCodec.TryRead(ref reader);
                    reader.Session.ReferencedTypes.RecordReferencedType(encoded);
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
                case SchemaType.WellKnown:
                    { 
                        var typeId = reader.ReadVarUInt32();
                        var found = reader.Session.WellKnownTypes.TryGetWellKnownType(typeId, out var type);
                        return (type, $"WellKnown {typeId} ({(found ? type is null ? "null" : RuntimeTypeNameFormatter.Format(type) : "unknown")})");
                    }
                case SchemaType.Encoded:
                    {
                        var found = reader.Session.TypeCodec.TryReadForAnalysis(ref reader, out Type encoded, out var typeString);
                        reader.Session.ReferencedTypes.RecordReferencedType(encoded);
                        return (encoded, $"Encoded \"{typeString}\" ({(found ? encoded is null ? "null" : RuntimeTypeNameFormatter.Format(encoded) : "not found")})");
                    }
                case SchemaType.Referenced:
                    {
                        var reference = reader.ReadVarUInt32();
                        var found = reader.Session.ReferencedTypes.TryGetReferencedType(reference, out var type);
                        return (type, $"Referenced {reference} ({(found ? type is null ? "null" : RuntimeTypeNameFormatter.Format(type) : "not found")})");
                    }
                default:
                    throw new ArgumentOutOfRangeException(nameof(schemaType));
            }
        }

        /// <summary>
        /// Reads a field header for diagnostic purposes.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <returns>The value which was read.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (Field Field, string Type) ReadFieldHeaderForAnalysis<TInput>(ref this Reader<TInput> reader)
        {
            Unsafe.SkipInit(out Field field);
            string type = default;
            var tag = (uint)reader.ReadByte();
            field.Tag = new(tag);
            if (tag < (byte)WireType.Extended && ((tag & Tag.FieldIdCompleteMask) == Tag.FieldIdCompleteMask || (tag & Tag.SchemaTypeMask) != (byte)SchemaType.Expected))
            {
                ReadFieldHeaderForAnalysisSlow(ref reader, ref field, ref type);
            }
            else
            {
                field.FieldIdDeltaRaw = tag & Tag.FieldIdMask;
                field.FieldTypeRaw = default;
                type = "Expected";
            }

            return (field, type);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadFieldHeaderForAnalysisSlow<TInput>(ref this Reader<TInput> reader, scoped ref Field field, scoped ref string type)
        {
            var fieldId = field.Tag.FieldIdDelta;
            if (fieldId == Tag.FieldIdCompleteMask)
            {
                field.FieldIdDeltaRaw = reader.ReadVarUInt32NoInlining();
            }
            else
            {
                field.FieldIdDeltaRaw = fieldId;
            }

            // If schema type is valid, read the type.
            var schemaType = field.Tag.SchemaType;
            if (schemaType != SchemaType.Expected)
            {
                (field.FieldTypeRaw, type) = reader.ReadTypeForAnalysis(schemaType);
            }
            else
            {
                field.FieldTypeRaw = default;
                type = "Expected";
            }
        }
    }
}
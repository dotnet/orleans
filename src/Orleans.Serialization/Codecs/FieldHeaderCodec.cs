using System;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.TypeSystem;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Buffers
{
    public ref partial struct Writer<TBufferWriter>
    {
        /// <summary>
        /// Writes the field header.
        /// </summary>
        /// <param name="fieldId">The field identifier.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="actualType">The actual type.</param>
        /// <param name="wireType">The wire type.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteFieldHeader(uint fieldId, Type expectedType, Type actualType, WireType wireType)
        {
            var embeddedFieldId = fieldId > Tag.MaxEmbeddedFieldIdDelta ? Tag.FieldIdCompleteMask : fieldId;
            var tag = (uint)wireType | embeddedFieldId;

            if (actualType is null || actualType == expectedType)
            {
                WriteByte((byte)tag); // SchemaType.Expected=0
                if (fieldId > Tag.MaxEmbeddedFieldIdDelta)
                {
                    WriteVarUInt32(fieldId);
                }
                return;
            }

            uint typeOrReferenceId;
            if (Session.WellKnownTypes.TryGetWellKnownTypeId(actualType, out var typeId))
            {
                typeOrReferenceId = typeId;
                tag |= (byte)SchemaType.WellKnown;
            }
            else if ((typeOrReferenceId = Session.ReferencedTypes.GetOrAddTypeReference(actualType)) != 0)
            {
                tag |= (byte)SchemaType.Referenced;
            }
            else
            {
                WriteFieldHeaderEncodeType(fieldId, actualType, tag);
                return;
            }

            WriteByte((byte)tag);
            if (fieldId > Tag.MaxEmbeddedFieldIdDelta)
                WriteVarUInt32(fieldId);

            WriteVarUInt32(typeOrReferenceId);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void WriteFieldHeaderEncodeType(uint fieldId, Type actualType, uint tag)
        {
            WriteByte((byte)(tag | (byte)SchemaType.Encoded));
            if (fieldId > Tag.MaxEmbeddedFieldIdDelta)
                WriteVarUInt32(fieldId);

            Session.TypeCodec.WriteEncodedType(ref this, actualType);
        }

        /// <summary>
        /// Writes an expected field header value.
        /// </summary>
        /// <param name="fieldId">The field identifier.</param>
        /// <param name="wireType">The wire type of the field.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteFieldHeaderExpected(uint fieldId, WireType wireType)
        {
            var embeddedFieldId = fieldId > Tag.MaxEmbeddedFieldIdDelta ? Tag.FieldIdCompleteMask : fieldId;
            WriteByte((byte)((uint)wireType | embeddedFieldId));

            if (fieldId > Tag.MaxEmbeddedFieldIdDelta)
                WriteVarUInt32(fieldId);
        }
    }
}

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Codec for operating with the wire format.
    /// </summary>
    public static class FieldHeaderCodec
    {
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
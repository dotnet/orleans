using System;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.WireProtocol
{
    /// <summary>
    /// Represents a field header.
    /// </summary>
    public struct Field
    {
        /// <summary>
        /// The tag byte.
        /// </summary>
        public Tag Tag;

        /// <summary>
        /// The raw field identifier delta.
        /// </summary>
        public uint FieldIdDeltaRaw;

        /// <summary>
        /// The raw field type.
        /// </summary>
        public Type FieldTypeRaw;

        /// <summary>
        /// Initializes a new instance of the <see cref="Field"/> struct.
        /// </summary>
        /// <param name="tag">The tag.</param>
        public Field(Tag tag)
        {
            Tag = tag;
            FieldIdDeltaRaw = 0;
            FieldTypeRaw = null;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Field"/> struct.
        /// </summary>
        /// <param name="tag">The tag.</param>
        /// <param name="extendedFieldIdDelta">The extended field identifier delta.</param>
        /// <param name="type">The type.</param>
        public Field(Tag tag, uint extendedFieldIdDelta, Type type)
        {
            Tag = tag;
            FieldIdDeltaRaw = extendedFieldIdDelta;
            FieldTypeRaw = type;
        }

        /// <summary>
        /// Gets or sets the field identifier delta.
        /// </summary>
        /// <value>The field identifier delta.</value>
        public uint FieldIdDelta
        {
            // If the embedded field id delta is valid, return it, otherwise return the extended field id delta.
            // The extended field id might not be valid if this field has the Extended wire type.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (Tag.IsFieldIdValid)
                {
                    return Tag.FieldIdDelta;
                }
#if DEBUG
                if (!HasFieldId)
                {
                    ThrowFieldIdInvalid();
                }
#endif
                return FieldIdDeltaRaw;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                // If the field id delta can fit into the tag, embed it there, otherwise invalidate the embedded field id delta and set the full field id delta.
                if (value < 7)
                {
                    Tag.FieldIdDelta = value;
                    FieldIdDeltaRaw = 0;
                }
                else
                {
                    Tag.SetFieldIdInvalid();
                    FieldIdDeltaRaw = value;
                }
            }
        }

        /// <summary>
        /// Gets or sets the type of the field.
        /// </summary>
        /// <value>The type of the field.</value>
        public Type FieldType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if DEBUG
                if (!IsSchemaTypeValid)
                {
                    ThrowFieldTypeInvalid();
                }
#endif
                return FieldTypeRaw;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
#if DEBUG
                if (!IsSchemaTypeValid)
                {
                    ThrowFieldTypeInvalid();
                }
#endif
                FieldTypeRaw = value;
            }
        }

        /// <summary>
        /// Gets a value indicating whether this instance has a field identifier.
        /// </summary>
        /// <value><see langword="true" /> if this instance has a field identifier; otherwise, <see langword="false" />.</value>
        public bool HasFieldId
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Tag.WireType != WireType.Extended;
        }

        /// <summary>
        /// Gets a value indicating whether this instance has an extended field identifier.
        /// </summary>
        /// <value><see langword="true" /> if this instance has an extended field identifier; otherwise, <see langword="false" />.</value>
        public bool HasExtendedFieldId
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Tag.HasExtendedFieldId;
        }

        /// <summary>
        /// Gets or sets the wire type.
        /// </summary>
        /// <value>The wire type.</value>
        public WireType WireType
        {
            get => Tag.WireType;
            set => Tag.WireType = value;
        }

        /// <summary>
        /// Gets or sets the schema type.
        /// </summary>
        /// <value>The schema type.</value>
        public SchemaType SchemaType
        {
            get
            {
#if DEBUG
                if (!IsSchemaTypeValid)
                {
                    ThrowSchemaTypeInvalid();
                }
#endif

                return Tag.SchemaType;
            }

            set => Tag.SchemaType = value;
        }

        /// <summary>
        /// Gets or sets the extended wire type.
        /// </summary>
        /// <value>The extended wire type.</value>
        public ExtendedWireType ExtendedWireType
        {
            get
            {
#if DEBUG
                if (WireType != WireType.Extended)
                {
                    ThrowExtendedWireTypeInvalid();
                }
#endif
                return Tag.ExtendedWireType;
            }
            set => Tag.ExtendedWireType = value;
        }

        /// <summary>
        /// Gets a value indicating whether this instance has a valid schema type.
        /// </summary>
        /// <value><see langword="true" /> if this instance has a valid schema; otherwise, <see langword="false" />.</value>
        public bool IsSchemaTypeValid => Tag.IsSchemaTypeValid;

        /// <summary>
        /// Gets a value indicating whether this instance has an extended schema type.
        /// </summary>
        /// <value><see langword="true" /> if this instance has an extended schema type; otherwise, <see langword="false" />.</value>
        public bool HasExtendedSchemaType => IsSchemaTypeValid && SchemaType != SchemaType.Expected;

        /// <summary>
        /// Gets a value indicating whether this instance represents the end of base fields in a tag-delimited structure.
        /// </summary>
        /// <value><see langword="true" /> if this instance represents end of base fields in a tag-delimited structure; otherwise, <see langword="false" />.</value>
        public bool IsEndBaseFields => Tag.HasExtendedWireType && Tag.ExtendedWireType == ExtendedWireType.EndBaseFields;

        /// <summary>
        /// Gets a value indicating whether this instance represents the end of a tag-delimited structure.
        /// </summary>
        /// <value><see langword="true" /> if this instance represents end of a tag-delimited structure; otherwise, <see langword="false" />.</value>
        public bool IsEndObject => Tag.HasExtendedWireType && Tag.ExtendedWireType == ExtendedWireType.EndTagDelimited;

        /// <summary>
        /// Gets a value indicating whether this instance represents the end of a tag-delimited structure or the end of base fields in a tag-delimited structure.
        /// </summary>
        /// <value><see langword="true" /> if this instance represents the end of a tag-delimited structure or the end of base fields in a tag-delimited structure; otherwise, <see langword="false" />.</value>
        public bool IsEndBaseOrEndObject
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Tag.HasExtendedWireType && Tag.ExtendedWireType <= ExtendedWireType.EndBaseFields;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            var builder = new DefaultInterpolatedStringHandler(0, 0);
            builder.AppendLiteral("[");
            builder.AppendFormatted(WireType);

            if (HasFieldId)
            {
                builder.AppendLiteral(", IdDelta:");
                builder.AppendFormatted(FieldIdDelta);
            }

            if (IsSchemaTypeValid)
            {
                builder.AppendLiteral(", SchemaType:");
                builder.AppendFormatted(SchemaType);
            }

            if (HasExtendedSchemaType)
            {
                builder.AppendLiteral(", RuntimeType:");
                builder.AppendFormatted(FieldType);
            }

            if (WireType == WireType.Extended)
            {
                builder.AppendLiteral(": ");
                builder.AppendFormatted(ExtendedWireType);
            }

            builder.AppendLiteral("]");
            return builder.ToStringAndClear();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowFieldIdInvalid() => throw new FieldIdNotPresentException();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowSchemaTypeInvalid() => throw new SchemaTypeInvalidException();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowFieldTypeInvalid() => throw new FieldTypeInvalidException();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowExtendedWireTypeInvalid() => throw new ExtendedWireTypeInvalidException();
    }
}
using System;
using System.Runtime.CompilerServices;
using System.Text;

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
            FieldIdDeltaRaw = tag.FieldIdDelta;
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
#if DEBUG
                if (!HasFieldId) throw new FieldIdNotPresentException();
#endif
                return FieldIdDeltaRaw;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                // If the field id delta can fit into the tag, embed it there, otherwise invalidate the embedded field id delta and set the full field id delta.
                if (value <= Tag.MaxEmbeddedFieldIdDelta)
                {
                    Tag.FieldIdDelta = value;
                }
                else
                {
                    Tag.SetFieldIdInvalid();
                }
                FieldIdDeltaRaw = value;
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
                    throw new FieldTypeInvalidException();
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
                    throw new FieldTypeInvalidException();
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
            get => !Tag.HasExtendedWireType;
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
                    throw new SchemaTypeInvalidException();
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
                    throw new ExtendedWireTypeInvalidException();
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
        public bool HasExtendedSchemaType => Tag.IsSchemaTypeValid && Tag.SchemaType != SchemaType.Expected;

        /// <summary>
        /// Gets a value indicating whether this instance represents the end of base fields in a tag-delimited structure.
        /// </summary>
        /// <value><see langword="true" /> if this instance represents end of base fields in a tag-delimited structure; otherwise, <see langword="false" />.</value>
        public bool IsEndBaseFields => Tag.IsEndBaseFields;

        /// <summary>
        /// Gets a value indicating whether this instance represents the end of a tag-delimited structure.
        /// </summary>
        /// <value><see langword="true" /> if this instance represents end of a tag-delimited structure; otherwise, <see langword="false" />.</value>
        public bool IsEndObject => Tag.IsEndObject;

        /// <summary>
        /// Gets a value indicating whether this instance represents the end of a tag-delimited structure or the end of base fields in a tag-delimited structure.
        /// </summary>
        /// <value><see langword="true" /> if this instance represents the end of a tag-delimited structure or the end of base fields in a tag-delimited structure; otherwise, <see langword="false" />.</value>
        public bool IsEndBaseOrEndObject
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Tag.HasExtendedWireType/* && Tag.ExtendedWireType <= ExtendedWireType.EndBaseFields*/;
        }

        /// <summary>
        /// Gets a value indicating whether this instance has a wire type of <see cref="WireType.Reference"/>.
        /// </summary>
        public bool IsReference => Tag.WireType == WireType.Reference;

        /// <summary>
        /// Ensures that the wire type is <see cref="WireType.TagDelimited"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureWireTypeTagDelimited()
        {
            if (Tag.WireType != WireType.TagDelimited)
                UnsupportedWireType();
        }

        private void UnsupportedWireType() => throw new UnsupportedWireTypeException($"A WireType value of {nameof(WireType.TagDelimited)} is expected by this codec. {this}");

        /// <summary>
        /// Ensures that the wire type is supported.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureWireType(WireType expectedType)
        {
            if (Tag.WireType != expectedType)
                UnsupportedWireType(expectedType);
        }

        private void UnsupportedWireType(WireType expectedType) => throw new UnsupportedWireTypeException($"A WireType value of {expectedType} is expected by this codec. {this}");

        /// <inheritdoc/>
        public override string ToString()
        {
#if NET6_0_OR_GREATER
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

            if (Tag.HasExtendedWireType)
            {
                builder.AppendLiteral(": ");
                builder.AppendFormatted(ExtendedWireType);
            }

            builder.AppendLiteral("]");
            return builder.ToStringAndClear();
#else
            var builder = new StringBuilder();
            builder.Append("[");
            builder.Append(WireType);

            if (HasFieldId)
            {
                builder.Append(", IdDelta:");
                builder.Append(FieldIdDelta);
            }

            if (IsSchemaTypeValid)
            {
                builder.Append(", SchemaType:");
                builder.Append(SchemaType);
            }

            if (HasExtendedSchemaType)
            {
                builder.Append(", RuntimeType:");
                builder.Append(FieldType);
            }

            if (WireType == WireType.Extended)
            {
                builder.Append(": ");
                builder.Append(ExtendedWireType);
            }

            builder.Append("]");
            return builder.ToString();
#endif
        }
    }
}
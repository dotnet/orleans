using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace Orleans.Serialization.WireProtocol
{
    public struct Field
    {
        public Tag Tag;
        public uint FieldIdDeltaRaw;
        public Type FieldTypeRaw;

        public Field(Tag tag)
        {
            Tag = tag;
            FieldIdDeltaRaw = 0;
            FieldTypeRaw = null;
        }

        public Field(Tag tag, uint extendedFieldIdDelta, Type type)
        {
            Tag = tag;
            FieldIdDeltaRaw = extendedFieldIdDelta;
            FieldTypeRaw = type;
        }

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

        public bool HasFieldId
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Tag.WireType != WireType.Extended;
        }

        public bool HasExtendedFieldId
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Tag.HasExtendedFieldId;
        }

        public WireType WireType
        {
            get => Tag.WireType;
            set => Tag.WireType = value;
        }

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

        public bool IsSchemaTypeValid => Tag.IsSchemaTypeValid;
        public bool HasExtendedSchemaType => IsSchemaTypeValid && SchemaType != SchemaType.Expected;
        public bool IsEndBaseFields => Tag.HasExtendedWireType && Tag.ExtendedWireType == ExtendedWireType.EndBaseFields;
        public bool IsEndObject => Tag.HasExtendedWireType && Tag.ExtendedWireType == ExtendedWireType.EndTagDelimited;

        public bool IsEndBaseOrEndObject
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Tag.HasExtendedWireType &&
                   (Tag.ExtendedWireType == ExtendedWireType.EndTagDelimited ||
                    Tag.ExtendedWireType == ExtendedWireType.EndBaseFields);
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            _ = builder.Append('[').Append((string)WireType.ToString());
            if (HasFieldId)
            {
                _ = builder.Append($", IdDelta:{FieldIdDelta}");
            }

            if (IsSchemaTypeValid)
            {
                _ = builder.Append($", SchemaType:{SchemaType}");
            }

            if (HasExtendedSchemaType)
            {
                _ = builder.Append($", RuntimeType:{FieldType}");
            }

            if (WireType == WireType.Extended)
            {
                _ = builder.Append($": {ExtendedWireType}");
            }

            _ = builder.Append(']');
            return builder.ToString();
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
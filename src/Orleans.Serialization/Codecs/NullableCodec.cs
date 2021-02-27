using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class NullableCodec<T> : IFieldCodec<T?> where T : struct
    {
        public static readonly Type CodecFieldType = typeof(T);
        private readonly IFieldCodec<T> _fieldCodec;

        public NullableCodec(IFieldCodec<T> fieldCodec)
        {
            _fieldCodec = OrleansGeneratedCodeHelper.UnwrapService(this, fieldCodec);
        }

        void IFieldCodec<T?>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, T? value)
        {
            // If the value is null, write it as the null reference.
            if (!value.HasValue && ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, null))
            {
                return;
            }

            // The value is not null.
            _fieldCodec.WriteField(ref writer, fieldIdDelta, CodecFieldType, value.Value);
        }

        T? IFieldCodec<T?>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            // This will only be true if the value is null.
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<T?, TInput>(ref reader, field);
            }

            // Read the non-null value.
            return _fieldCodec.ReadValue(ref reader, field);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.TagDelimited} is supported for string fields. {field}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowIndexOutOfRangeException(int length) => throw new IndexOutOfRangeException(
            $"Encountered too many elements in array of type {typeof(T?)} with declared length {length}.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowLengthFieldMissing() => throw new RequiredFieldMissingException("Serialized array is missing its length field.");
    }

    [RegisterCopier]
    public sealed class NullableCopier<T> : IDeepCopier<T?> where T : struct
    {
        private readonly IDeepCopier<T> _copier;
        public NullableCopier(IDeepCopier<T> copier)
        {
            _copier = copier;
        }

        public T? DeepCopy(T? input, CopyContext context)
        {
            if (!input.HasValue)
            {
                return input;
            }

            return new T?(_copier.DeepCopy(input.Value, context));
        }
    }
}
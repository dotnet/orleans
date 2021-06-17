using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;
using System;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Codecs
{
    public sealed class VoidCodec : IFieldCodec<object>
    {
        void IFieldCodec<object>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value)
        {
            if (!ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                ThrowNotNullException(value);
            }
        }

        object IFieldCodec<object>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType != WireType.Reference)
            {
                ThrowInvalidWireType(field);
            }

            return ReferenceCodec.ReadReference<object, TInput>(ref reader, field);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidWireType(Field field) => throw new UnsupportedWireTypeException($"Expected a reference, but encountered wire type of '{field.WireType}'.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowNotNullException(object value) => throw new InvalidOperationException(
            $"Expected a value of null, but encountered a value of '{value}'.");
    }

    public sealed class VoidCopier : IDeepCopier<object>
    {
        public object DeepCopy(object input, CopyContext context)
        {
            if (context.TryGetCopy<object>(input, out var result))
            {
                return result;
            }

            ThrowNotNullException(input);
            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowNotNullException(object value) => throw new InvalidOperationException($"Expected a value of null, but encountered a value of type '{value.GetType()}'.");
    }
}
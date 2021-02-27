using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Codec for <see cref="Queue{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterSerializer]
    public sealed class QueueCodec<T> : IFieldCodec<Queue<T>>
    {
        public static readonly Type CodecElementType = typeof(T);
        private readonly IFieldCodec<T> _fieldCodec;

        public QueueCodec(IFieldCodec<T> fieldCodec)
        {
            _fieldCodec = OrleansGeneratedCodeHelper.UnwrapService(this, fieldCodec);
        }

        void IFieldCodec<Queue<T>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Queue<T> value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);

            Int32Codec.WriteField(ref writer, 0, Int32Codec.CodecFieldType, value.Count);
            uint innerFieldIdDelta = 1;
            foreach (var element in value)
            {
                _fieldCodec.WriteField(ref writer, innerFieldIdDelta, CodecElementType, element);
                innerFieldIdDelta = 0;
            }

            writer.WriteEndObject();
        }

        Queue<T> IFieldCodec<Queue<T>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<Queue<T>, TInput>(ref reader, field);
            }

            if (field.WireType != WireType.TagDelimited)
            {
                ThrowUnsupportedWireTypeException(field);
            }

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            Queue<T> result = null;
            uint fieldId = 0;
            var length = 0;
            var index = 0;
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject)
                {
                    break;
                }

                fieldId += header.FieldIdDelta;
                switch (fieldId)
                {
                    case 0:
                        length = Int32Codec.ReadValue(ref reader, header);
                        result = new Queue<T>(length);
                        ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
                        break;
                    case 1:
                        if (result is null)
                        {
                            ThrowLengthFieldMissing();
                        }

                        if (index >= length)
                        {
                            ThrowIndexOutOfRangeException(length);
                        }
                        result.Enqueue(_fieldCodec.ReadValue(ref reader, header));
                        ++index;
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.TagDelimited} is supported for string fields. {field}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowIndexOutOfRangeException(int length) => throw new IndexOutOfRangeException(
            $"Encountered too many elements in array of type {typeof(Queue<T>)} with declared length {length}.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowLengthFieldMissing() => throw new RequiredFieldMissingException("Serialized array is missing its length field.");
    }

    [RegisterCopier]
    public sealed class QueueCopier<T> : IDeepCopier<Queue<T>>, IBaseCopier<Queue<T>>
    {
        private readonly IDeepCopier<T> _copier;

        public QueueCopier(IDeepCopier<T> valueCopier)
        {
            _copier = valueCopier;
        }

        public Queue<T> DeepCopy(Queue<T> input, CopyContext context)
        {
            if (context.TryGetCopy<Queue<T>>(input, out var result))
            {
                return result;
            }

            if (input.GetType() != typeof(Queue<T>))
            {
                return context.Copy(input);
            }

            result = new Queue<T>(input.Count);
            context.RecordCopy(input, result);
            foreach (var item in input)
            {
                result.Enqueue(_copier.DeepCopy(item, context));
            }

            return result;
        }

        public void DeepCopy(Queue<T> input, Queue<T> output, CopyContext context)
        {
            foreach (var item in input)
            {
                output.Enqueue(_copier.DeepCopy(item, context));
            }
        }
    }
}
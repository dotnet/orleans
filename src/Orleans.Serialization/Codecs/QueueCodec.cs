using System;
using System.Buffers;
using System.Collections.Generic;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="Queue{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterSerializer]
    public sealed class QueueCodec<T> : IFieldCodec<Queue<T>>
    {
        private readonly Type CodecElementType = typeof(T);
        private readonly IFieldCodec<T> _fieldCodec;

        /// <summary>
        /// Initializes a new instance of the <see cref="QueueCodec{T}"/> class.
        /// </summary>
        /// <param name="fieldCodec">The field codec.</param>
        public QueueCodec(IFieldCodec<T> fieldCodec)
        {
            _fieldCodec = OrleansGeneratedCodeHelper.UnwrapService(this, fieldCodec);
        }

        /// <inheritdoc/>
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Queue<T> value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);

            if (value.Count > 0)
            {
                UInt32Codec.WriteField(ref writer, 0, (uint)value.Count);
                uint innerFieldIdDelta = 1;
                foreach (var element in value)
                {
                    _fieldCodec.WriteField(ref writer, innerFieldIdDelta, CodecElementType, element);
                    innerFieldIdDelta = 0;
                }
            }

            writer.WriteEndObject();
        }

        /// <inheritdoc/>
        public Queue<T> ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<Queue<T>, TInput>(ref reader, field);
            }

            field.EnsureWireTypeTagDelimited();

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            Queue<T> result = null;
            uint fieldId = 0;
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
                        var length = (int)UInt32Codec.ReadValue(ref reader, header);
                        result = new Queue<T>(length);
                        ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
                        break;
                    case 1:
                        if (result is null)
                        {
                            ThrowLengthFieldMissing();
                        }

                        result.Enqueue(_fieldCodec.ReadValue(ref reader, header));
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            if (result is null)
            {
                result = new();
                ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            }

            return result;
        }

        private void ThrowLengthFieldMissing() => throw new RequiredFieldMissingException("Serialized queue is missing its length field.");
    }

    /// <summary>
    /// Copier for <see cref="Queue{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterCopier]
    public sealed class QueueCopier<T> : IDeepCopier<Queue<T>>, IBaseCopier<Queue<T>>
    {
        private readonly Type _fieldType = typeof(Queue<T>);
        private readonly IDeepCopier<T> _copier;

        /// <summary>
        /// Initializes a new instance of the <see cref="QueueCopier{T}"/> class.
        /// </summary>
        /// <param name="valueCopier">The value copier.</param>
        public QueueCopier(IDeepCopier<T> valueCopier)
        {
            _copier = valueCopier;
        }

        /// <inheritdoc/>
        public Queue<T> DeepCopy(Queue<T> input, CopyContext context)
        {
            if (context.TryGetCopy<Queue<T>>(input, out var result))
            {
                return result;
            }

            if (input.GetType() as object != _fieldType as object)
            {
                return context.DeepCopy(input);
            }

            result = new Queue<T>(input.Count);
            context.RecordCopy(input, result);
            foreach (var item in input)
            {
                result.Enqueue(_copier.DeepCopy(item, context));
            }

            return result;
        }

        /// <inheritdoc/>
        public void DeepCopy(Queue<T> input, Queue<T> output, CopyContext context)
        {
            foreach (var item in input)
            {
                output.Enqueue(_copier.DeepCopy(item, context));
            }
        }
    }
}
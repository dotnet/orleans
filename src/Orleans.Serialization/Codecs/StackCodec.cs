using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="Stack{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterSerializer]
    public sealed class StackCodec<T> : IFieldCodec<Stack<T>>
    {
        private readonly Type CodecElementType = typeof(T);

        private readonly IFieldCodec<T> _fieldCodec;

        /// <summary>
        /// Initializes a new instance of the <see cref="StackCodec{T}"/> class.
        /// </summary>
        /// <param name="fieldCodec">The field codec.</param>
        public StackCodec(IFieldCodec<T> fieldCodec)
        {
            _fieldCodec = OrleansGeneratedCodeHelper.UnwrapService(this, fieldCodec);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Stack<T> value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);

            if (value.Count > 0)
            {
                UInt32Codec.WriteField(ref writer, 0, UInt32Codec.CodecFieldType, (uint)value.Count);
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
        public Stack<T> ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<Stack<T>, TInput>(ref reader, field);
            }

            field.EnsureWireTypeTagDelimited();

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            Stack<T> result = null;
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
                        if (length > 10240 && length > reader.Length)
                        {
                            ThrowInvalidSizeException(length);
                        }

                        result = new(length);
                        ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
                        break;
                    case 1:
                        if (result is null)
                        {
                            ThrowLengthFieldMissing();
                        }

                        result.Push(_fieldCodec.ReadValue(ref reader, header));
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

        private static void ThrowInvalidSizeException(int length) => throw new IndexOutOfRangeException(
            $"Declared length of {typeof(Stack<T>)}, {length}, is greater than total length of input.");

        private static void ThrowLengthFieldMissing() => throw new RequiredFieldMissingException("Serialized stack is missing its length field.");
    }

    /// <summary>
    /// Copier for <see cref="Stack{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterCopier]
    public sealed class StackCopier<T> : IDeepCopier<Stack<T>>, IBaseCopier<Stack<T>>
    {
        private readonly IDeepCopier<T> _copier;

        /// <summary>
        /// Initializes a new instance of the <see cref="StackCopier{T}"/> class.
        /// </summary>
        /// <param name="valueCopier">The value copier.</param>
        public StackCopier(IDeepCopier<T> valueCopier)
        {
            _copier = valueCopier;
        }

        /// <inheritdoc/>
        public Stack<T> DeepCopy(Stack<T> input, CopyContext context)
        {
            if (context.TryGetCopy<Stack<T>>(input, out var result))
            {
                return result;
            }

            if (input.GetType() != typeof(Stack<T>))
            {
                return context.DeepCopy(input);
            }

            result = new Stack<T>(input.Count);
            context.RecordCopy(input, result);
            foreach (var item in input)
            {
                result.Push(_copier.DeepCopy(item, context));
            }

            return result;
        }

        /// <inheritdoc/>
        public void DeepCopy(Stack<T> input, Stack<T> output, CopyContext context)
        {
            foreach (var item in input)
            {
                output.Push(_copier.DeepCopy(item, context));
            }
        }
    }
}

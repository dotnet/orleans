using System;
using System.Buffers;
using System.Collections.Generic;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="HashSet{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterSerializer]
    public sealed class HashSetCodec<T> : IFieldCodec<HashSet<T>>
    {
        private readonly Type CodecElementType = typeof(T);
        private readonly Type _comparerType = typeof(IEqualityComparer<T>);

        private readonly IFieldCodec<T> _fieldCodec;
        private readonly IFieldCodec<IEqualityComparer<T>> _comparerCodec;

        /// <summary>
        /// Initializes a new instance of the <see cref="HashSetCodec{T}"/> class.
        /// </summary>
        /// <param name="fieldCodec">The field codec.</param>
        /// <param name="comparerCodec">The comparer codec.</param>
        public HashSetCodec(IFieldCodec<T> fieldCodec, IFieldCodec<IEqualityComparer<T>> comparerCodec)
        {
            _fieldCodec = OrleansGeneratedCodeHelper.UnwrapService(this, fieldCodec);
            _comparerCodec = OrleansGeneratedCodeHelper.UnwrapService(this, comparerCodec);
        }

        /// <inheritdoc/>
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, HashSet<T> value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);

            if (value.Comparer != EqualityComparer<T>.Default)
            {
                _comparerCodec.WriteField(ref writer, 0, _comparerType, value.Comparer);
            }

            if (value.Count > 0)
            {
                UInt32Codec.WriteField(ref writer, 1, (uint)value.Count);
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
        public HashSet<T> ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<HashSet<T>, TInput>(ref reader, field);
            }

            field.EnsureWireTypeTagDelimited();

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            HashSet<T> result = null;
            IEqualityComparer<T> comparer = null;
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
                        comparer = _comparerCodec.ReadValue(ref reader, header);
                        break;
                    case 1:
                        var length = (int)UInt32Codec.ReadValue(ref reader, header);
                        if (length > 10240 && length > reader.Length)
                        {
                            ThrowInvalidSizeException(length);
                        }

                        result = CreateInstance(length, comparer, reader.Session, placeholderReferenceId);
                        break;
                    case 2:
                        if (result is null)
                            ThrowLengthFieldMissing();

                        result.Add(_fieldCodec.ReadValue(ref reader, header));
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            result ??= CreateInstance(0, comparer, reader.Session, placeholderReferenceId);
            return result;
        }

        private HashSet<T> CreateInstance(int length, IEqualityComparer<T> comparer, SerializerSession session, uint placeholderReferenceId)
        {
            var result = new HashSet<T>(length, comparer);
            ReferenceCodec.RecordObject(session, result, placeholderReferenceId);
            return result;
        }

        private void ThrowInvalidSizeException(int length) => throw new IndexOutOfRangeException(
            $"Declared length of {typeof(HashSet<T>)}, {length}, is greater than total length of input.");

        private void ThrowLengthFieldMissing() => throw new RequiredFieldMissingException("Serialized set is missing its length field.");
    }

    /// <summary>
    /// Copier for <see cref="HashSet{T}"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    [RegisterCopier]
    public sealed class HashSetCopier<T> : IDeepCopier<HashSet<T>>, IBaseCopier<HashSet<T>>
    {
        private readonly Type _fieldType = typeof(HashSet<T>);
        private readonly IDeepCopier<T> _copier;

        /// <summary>
        /// Initializes a new instance of the <see cref="HashSetCopier{T}"/> class.
        /// </summary>
        /// <param name="valueCopier">The value copier.</param>
        public HashSetCopier(IDeepCopier<T> valueCopier)
        {
            _copier = valueCopier;
        }

        /// <inheritdoc/>
        public HashSet<T> DeepCopy(HashSet<T> input, CopyContext context)
        {
            if (context.TryGetCopy<HashSet<T>>(input, out var result))
            {
                return result;
            }

            if (input.GetType() as object != _fieldType as object)
            {
                return context.DeepCopy(input);
            }

            result = new(input.Count, input.Comparer);
            context.RecordCopy(input, result);
            foreach (var item in input)
            {
                result.Add(_copier.DeepCopy(item, context));
            }

            return result;
        }

        /// <inheritdoc/>
        public void DeepCopy(HashSet<T> input, HashSet<T> output, CopyContext context)
        {
            foreach (var item in input)
            {
                output.Add(_copier.DeepCopy(item, context));
            }
        }
    }
}

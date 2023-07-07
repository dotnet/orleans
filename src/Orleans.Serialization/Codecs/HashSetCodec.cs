using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="HashSet{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterSerializer]
    public sealed class HashSetCodec<T> : IFieldCodec<HashSet<T>>, IBaseCodec<HashSet<T>>
    {
        private readonly Type CodecElementType = typeof(T);
        private readonly Type _comparerType = typeof(IEqualityComparer<T>);
        private readonly ConstructorInfo _baseConstructor;

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
            _baseConstructor = typeof(HashSet<T>).GetConstructor([typeof(int), typeof(IEqualityComparer<T>)])!;
        }

        /// <inheritdoc/>
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, HashSet<T> value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);

            Serialize(ref writer, value);

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

        private static HashSet<T> CreateInstance(int length, IEqualityComparer<T> comparer, SerializerSession session, uint placeholderReferenceId)
        {
            var result = new HashSet<T>(length, comparer);
            ReferenceCodec.RecordObject(session, result, placeholderReferenceId);
            return result;
        }

        private static void ThrowInvalidSizeException(int length) => throw new IndexOutOfRangeException(
            $"Declared length of {typeof(HashSet<T>)}, {length}, is greater than total length of input.");

        private static void ThrowLengthFieldMissing() => throw new RequiredFieldMissingException("Serialized set is missing its length field.");

        public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, HashSet<T> value) where TBufferWriter : IBufferWriter<byte>
        {
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
        }

        void IBaseCodec<HashSet<T>>.Deserialize<TInput>(ref Reader<TInput> reader, HashSet<T> value)
        {
            // If the value has some values added by the constructor, clear them.
            // If those values are in the serialized payload, they will be added below.
            value.Clear();

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

                        // Re-initialize the class by calling the constructor.
                        if (comparer is not null)
                        {
                            _baseConstructor.Invoke(value, [length, comparer]);
                        }
                        else
                        {
                            value.EnsureCapacity(length);
                        }

                        break;
                    case 2:
                        value.Add(_fieldCodec.ReadValue(ref reader, header));
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

        }
    }

    /// <summary>
    /// Copier for <see cref="HashSet{T}"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <remarks>
    /// Initializes a new instance of the <see cref="HashSetCopier{T}"/> class.
    /// </remarks>
    /// <param name="valueCopier">The value copier.</param>
    [RegisterCopier]
    public sealed class HashSetCopier<T>(IDeepCopier<T> valueCopier) : IDeepCopier<HashSet<T>>, IBaseCopier<HashSet<T>>
    {
        private readonly Type _fieldType = typeof(HashSet<T>);
        private readonly IDeepCopier<T> _copier = valueCopier;
        private readonly ConstructorInfo _baseConstructor = typeof(HashSet<T>).GetConstructor([typeof(int), typeof(IEqualityComparer<T>)])!;

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
            // If the value has some values added by the constructor, clear them.
            // If those values are in the serialized payload, they will be added below.
            output.Clear();
            if (input.Comparer != EqualityComparer<T>.Default)
            {
                _baseConstructor.Invoke(output, [input.Count, input.Comparer]);
            }
            else
            {
                output.EnsureCapacity(input.Count);
            }

            foreach (var item in input)
            {
                output.Add(_copier.DeepCopy(item, context));
            }
        }
    }
}

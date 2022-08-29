using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="ImmutableStack{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterSerializer]
    public sealed class ImmutableStackCodec<T> : GeneralizedReferenceTypeSurrogateCodec<ImmutableStack<T>, ImmutableStackSurrogate<T>>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ImmutableStackCodec{T}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public ImmutableStackCodec(IValueSerializer<ImmutableStackSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc/>
        public override ImmutableStack<T> ConvertFromSurrogate(ref ImmutableStackSurrogate<T> surrogate) => surrogate.Values switch
        {
            null => default,
            object => ImmutableStack.CreateRange(surrogate.Values)
        };

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ImmutableStack<T> value, ref ImmutableStackSurrogate<T> surrogate) => surrogate = value switch
        {
            null => default,
            _ => new ImmutableStackSurrogate<T>
            {
                Values = new List<T>(value)
            },
        };
    }

    /// <summary>
    /// Surrogate type for <see cref="ImmutableStackCodec{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [GenerateSerializer]
    public struct ImmutableStackSurrogate<T>
    {
        /// <summary>
        /// Gets or sets the values.
        /// </summary>
        /// <value>The values.</value>
        [Id(1)]
        public List<T> Values { get; set; }
    }

    /// <summary>
    /// Copier for <see cref="ImmutableStack{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterCopier]
    public sealed class ImmutableStackCopier<T> : IDeepCopier<ImmutableStack<T>>
    {
        /// <inheritdoc/>
        public ImmutableStack<T> DeepCopy(ImmutableStack<T> input, CopyContext _) => input;
    }

    /// <summary>
    /// Serializer for <see cref="Stack{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterSerializer]
    public sealed class StackCodec<T> : IFieldCodec<Stack<T>>
    {
        private static readonly Type CodecElementType = typeof(T);

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

            Int32Codec.WriteField(ref writer, 0, Int32Codec.CodecFieldType, value.Count);
            uint innerFieldIdDelta = 1;
            foreach (var element in value)
            {
                _fieldCodec.WriteField(ref writer, innerFieldIdDelta, CodecElementType, element);
                innerFieldIdDelta = 0;
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

            if (field.WireType != WireType.TagDelimited)
            {
                ThrowUnsupportedWireTypeException(field);
            }

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            Stack<T> result = null;
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
                        if (length > 10240 && length > reader.Length)
                        {
                            ThrowInvalidSizeException(length);
                        }

                        result = new Stack<T>(length);
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
                        result.Push(_fieldCodec.ReadValue(ref reader, header));
                        ++index;
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            return result;
        }

        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.TagDelimited} is supported for string fields. {field}");

        private static void ThrowIndexOutOfRangeException(int length) => throw new IndexOutOfRangeException(
            $"Encountered too many elements in array of type {typeof(Stack<T>)} with declared length {length}.");

        private static void ThrowInvalidSizeException(int length) => throw new IndexOutOfRangeException(
            $"Declared length of {typeof(Stack<T>)}, {length}, is greater than total length of input.");

        private static void ThrowLengthFieldMissing() => throw new RequiredFieldMissingException("Serialized array is missing its length field.");
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

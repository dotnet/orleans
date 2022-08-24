using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for multi-dimensional arrays.
    /// </summary>
    /// <typeparam name="T">The array element type.</typeparam>
    internal sealed class MultiDimensionalArrayCodec<T> : IGeneralizedCodec
    {
        private static readonly Type DimensionFieldType = typeof(int[]);
        private static readonly Type CodecElementType = typeof(T);

        private readonly IFieldCodec<int[]> _intArrayCodec;
        private readonly IFieldCodec<T> _elementCodec;

        /// <summary>
        /// Initializes a new instance of the <see cref="MultiDimensionalArrayCodec{T}"/> class.
        /// </summary>
        /// <param name="intArrayCodec">The int array codec.</param>
        /// <param name="elementCodec">The element codec.</param>
        public MultiDimensionalArrayCodec(IFieldCodec<int[]> intArrayCodec, IFieldCodec<T> elementCodec)
        {
            _intArrayCodec = OrleansGeneratedCodeHelper.UnwrapService(this, intArrayCodec);
            _elementCodec = OrleansGeneratedCodeHelper.UnwrapService(this, elementCodec);
        }

        /// <inheritdoc/>
        void IFieldCodec<object>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);

            var array = (Array)value;
            var rank = array.Rank;

            var lengths = new int[rank];
            var indices = new int[rank];

            // Write array lengths.
            for (var i = 0; i < rank; i++)
            {
                lengths[i] = array.GetLength(i);
            }

            _intArrayCodec.WriteField(ref writer, 0, DimensionFieldType, lengths);

            var remaining = array.Length;
            uint innerFieldIdDelta = 1;
            while (remaining-- > 0)
            {
                var element = array.GetValue(indices);
                _elementCodec.WriteField(ref writer, innerFieldIdDelta, CodecElementType, (T)element);
                innerFieldIdDelta = 0;

                // Increment the indices array by 1.
                if (remaining > 0)
                {
                    var idx = rank - 1;
                    while (idx >= 0 && ++indices[idx] >= lengths[idx])
                    {
                        indices[idx] = 0;
                        --idx;
                        if (idx < 0)
                        {
                            _ = ThrowIndexOutOfRangeException(lengths);
                        }
                    }
                }
            }


            writer.WriteEndObject();
        }

        /// <inheritdoc/>
        object IFieldCodec<object>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<T[], TInput>(ref reader, field);
            }

            if (field.WireType != WireType.TagDelimited)
            {
                ThrowUnsupportedWireTypeException(field);
            }

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            Array result = null;
            uint fieldId = 0;
            int[] lengths = null;
            int[] indices = null;
            var rank = 0;
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
                        {
                            lengths = _intArrayCodec.ReadValue(ref reader, header);
                            rank = lengths.Length;

                            // Multi-dimensional arrays must be indexed using indexing arrays, so create one now.
                            indices = new int[rank];
                            result = Array.CreateInstance(CodecElementType, lengths);
                            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
                            break;
                        }
                    case 1:
                        {
                            if (result is null || indices is null || lengths is null)
                            {
                                return ThrowLengthsFieldMissing();
                            }

                            var element = _elementCodec.ReadValue(ref reader, header);
                            result.SetValue(element, indices);

                            // Increment the indices array by 1.
                            var idx = rank - 1;
                            while (idx >= 0 && ++indices[idx] >= lengths[idx])
                            {
                                indices[idx] = 0;
                                --idx;
                            }

                            break;
                        }
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            return result;
        }

        /// <inheritdoc/>
        public bool IsSupportedType(Type type) => type.IsArray && type.GetArrayRank() > 1;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object ThrowIndexOutOfRangeException(int[] lengths) => throw new IndexOutOfRangeException(
            $"Encountered too many elements in array of type {typeof(T)} with declared lengths {string.Join(", ", lengths)}.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.TagDelimited} is supported for string fields. {field}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T ThrowLengthsFieldMissing() => throw new RequiredFieldMissingException("Serialized array is missing its lengths field.");
    }

    /// <summary>
    /// Copier for multi-dimensional arrays.
    /// </summary>
    /// <typeparam name="T">The array element type.</typeparam>
    internal sealed class MultiDimensionalArrayCopier<T> : IGeneralizedCopier
    {
        private readonly IDeepCopier<object> _elementCopier;

        /// <summary>
        /// Initializes a new instance of the <see cref="MultiDimensionalArrayCopier{T}"/> class.
        /// </summary>
        /// <param name="elementCopier">The element copier.</param>
        public MultiDimensionalArrayCopier(IDeepCopier<object> elementCopier)
        {
            _elementCopier = OrleansGeneratedCodeHelper.UnwrapService(this, elementCopier);
        }

        /// <inheritdoc/>
        object IDeepCopier<object>.DeepCopy(object original, CopyContext context)
        {
            if (context.TryGetCopy<Array>(original, out var result))
            {
                return result;
            }

            var type = original.GetType();
            var originalArray = (Array)original;
            var elementType = type.GetElementType();
            if (ShallowCopyableTypes.Contains(elementType))
            {
                return originalArray.Clone();
            }

            // We assume that all arrays have lower bound 0. In .NET 4.0, it's hard to create an array with a non-zero lower bound.
            var rank = originalArray.Rank;
            var lengths = new int[rank];
            for (var i = 0; i < rank; i++)
            {
                lengths[i] = originalArray.GetLength(i);
            }

            result = Array.CreateInstance(elementType, lengths);
            context.RecordCopy(original, result); 

            if (rank == 1)
            {
                for (var i = 0; i < lengths[0]; i++)
                {
                    result.SetValue(_elementCopier.DeepCopy(originalArray.GetValue(i), context), i);
                }
            }
            else if (rank == 2)
            {
                for (var i = 0; i < lengths[0]; i++)
                {
                    for (var j = 0; j < lengths[1]; j++)
                    {
                        result.SetValue(_elementCopier.DeepCopy(originalArray.GetValue(i, j), context), i, j);
                    }
                }
            }
            else
            {
                var index = new int[rank];
                var sizes = new int[rank];
                sizes[rank - 1] = 1;
                for (var k = rank - 2; k >= 0; k--)
                {
                    sizes[k] = sizes[k + 1] * lengths[k + 1];
                }

                for (var i = 0; i < originalArray.Length; i++)
                {
                    int k = i;
                    for (int n = 0; n < rank; n++)
                    {
                        int offset = k / sizes[n];
                        k -= offset * sizes[n];
                        index[n] = offset;
                    }

                    result.SetValue(_elementCopier.DeepCopy(originalArray.GetValue(index), context), index);
                }
            }

            return result;
        }

        /// <inheritdoc/>
        public bool IsSupportedType(Type type) => type.IsArray && type.GetArrayRank() > 1;
    }
}
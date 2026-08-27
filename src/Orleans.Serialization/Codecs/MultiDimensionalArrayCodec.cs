using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for multi-dimensional arrays.
    /// </summary>
    /// <typeparam name="T">The array element type.</typeparam>
    internal sealed class MultiDimensionalArrayCodec<T> : IGeneralizedCodec
    {
        private readonly Type DimensionFieldType = typeof(int[]);
        private readonly Type CodecElementType = typeof(T);

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
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, [System.Diagnostics.CodeAnalysis.AllowNull] Type expectedType, [System.Diagnostics.CodeAnalysis.AllowNull] object? value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value is Array input)
            {
                EnsureZeroLowerBounds(input);
            }

            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value!))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, value!.GetType(), WireType.TagDelimited);

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
                _elementCodec.WriteField(ref writer, innerFieldIdDelta, CodecElementType, (T)element!);
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
                            ThrowIndexOutOfRangeException(lengths);
                        }
                    }
                }
            }


            writer.WriteEndObject();
        }

        /// <inheritdoc/>
        [return: System.Diagnostics.CodeAnalysis.MaybeNull]
        public object? ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<T[], TInput>(ref reader, field);
            }

            field.EnsureWireTypeTagDelimited();

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            Array? result = null;
            uint fieldId = 0;
            int[]? lengths = null;
            int[]? indices = null;
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
                            lengths = _intArrayCodec.ReadValue(ref reader, header)!;
                            rank = lengths.Length;
                            EnsureSufficientData(ref reader, lengths);

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
                                ThrowLengthsFieldMissing();
                            }

                            var element = _elementCodec.ReadValue(ref reader, header);
                            result!.SetValue(element, indices!);

                            // Increment the indices array by 1.
                            var idx = rank - 1;
                            while (idx >= 0 && ++indices![idx] >= lengths![idx])
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
        public bool IsSupportedType(Type type) => type.IsArray && !type.IsSZArray;

        private void ThrowIndexOutOfRangeException(int[] lengths) => throw new IndexOutOfRangeException(
            $"Encountered too many elements in array of type {CodecElementType} with declared lengths {string.Join(", ", lengths)}.");

        private static void EnsureSufficientData<TInput>(ref Reader<TInput> reader, int[] lengths)
        {
            var remaining = (ulong)reader.Remaining;
            ulong elementCount = 1;
            foreach (var length in lengths)
            {
                if (length < 0)
                {
                    return;
                }

                if (length != 0 && elementCount > remaining / (uint)length)
                {
                    ThrowInvalidSizeException(lengths, reader.Remaining);
                }

                elementCount *= (uint)length;
            }
        }

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidSizeException(int[] lengths, long remaining) => throw new IndexOutOfRangeException(
            $"Declared dimensions [{string.Join(", ", lengths)}] require more elements than the remaining length of the input, {remaining}.");

        private static void ThrowLengthsFieldMissing() => throw new RequiredFieldMissingException("Serialized array is missing its lengths field.");

        private static void EnsureZeroLowerBounds(Array array)
        {
            for (var i = 0; i < array.Rank; i++)
            {
                if (array.GetLowerBound(i) != 0)
                {
                    ThrowNonZeroLowerBoundsNotSupported();
                }
            }
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowNonZeroLowerBoundsNotSupported() => throw new NotSupportedException(
            "Serialization of multi-dimensional arrays with non-zero lower bounds is not supported.");
    }

    /// <summary>
    /// Copier for multi-dimensional arrays.
    /// </summary>
    /// <typeparam name="T">The array element type.</typeparam>
    internal sealed class MultiDimensionalArrayCopier<T> : IGeneralizedCopier
    {
        /// <inheritdoc/>
        [return: NotNullIfNotNull(nameof(original))]
        public object? DeepCopy(object? original, CopyContext context)
        {
            if (context.TryGetCopy<Array>(original!, out var result))
            {
                return result;
            }

            var type = original!.GetType();
            var originalArray = (Array)original;
            var elementType = type.GetElementType();
            if (ShallowCopyableTypes.Contains(elementType!))
            {
                result = (Array)originalArray.Clone();
                context.RecordCopy(original, result);
                return result;
            }

            var rank = originalArray.Rank;
            var lengths = new int[rank];
            var lowerBounds = new int[rank];
            for (var i = 0; i < rank; i++)
            {
                lengths[i] = originalArray.GetLength(i);
                lowerBounds[i] = originalArray.GetLowerBound(i);
            }

            result = Array.CreateInstance(elementType!, lengths, lowerBounds);
            context.RecordCopy(original, result);

            if (rank == 1)
            {
                for (var offset = 0; offset < lengths[0]; offset++)
                {
                    var i = lowerBounds[0] + offset;
                    result.SetValue(ObjectCopier.DeepCopy(originalArray.GetValue(i), context), i);
                }
            }
            else if (rank == 2)
            {
                for (var iOffset = 0; iOffset < lengths[0]; iOffset++)
                {
                    var i = lowerBounds[0] + iOffset;
                    for (var jOffset = 0; jOffset < lengths[1]; jOffset++)
                    {
                        var j = lowerBounds[1] + jOffset;
                        result.SetValue(ObjectCopier.DeepCopy(originalArray.GetValue(i, j), context), i, j);
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
                        index[n] = offset + lowerBounds[n];
                    }

                    result.SetValue(ObjectCopier.DeepCopy(originalArray.GetValue(index), context), index);
                }
            }

            return result;
        }

        /// <inheritdoc/>
        public bool IsSupportedType(Type type) => type.IsArray && !type.IsSZArray;
    }
}

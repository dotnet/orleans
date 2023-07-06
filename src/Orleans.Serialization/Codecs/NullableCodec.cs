using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="Nullable{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterSerializer]
    public sealed class NullableCodec<T> : IFieldCodec<T?> where T : struct
    {
        private readonly Type CodecFieldType = typeof(T);
        private readonly IFieldCodec<T> _fieldCodec;

        /// <summary>
        /// Initializes a new instance of the <see cref="NullableCodec{T}"/> class.
        /// </summary>
        /// <param name="fieldCodec">The field codec.</param>
        public NullableCodec(IFieldCodec<T> fieldCodec)
        {
            _fieldCodec = OrleansGeneratedCodeHelper.UnwrapService(this, fieldCodec);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, T? value) where TBufferWriter : IBufferWriter<byte>
        {
            // If the value is null, write it as the null reference.
            if (value is null)
            {
                ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
                return;
            }

            // The value is not null.
            _fieldCodec.WriteField(ref writer, fieldIdDelta, CodecFieldType, value.GetValueOrDefault());
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T? ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            // This will only be true if the value is null.
            if (field.WireType == WireType.Reference)
            {
                ReferenceCodec.MarkValueField(reader.Session);
                var reference = reader.ReadVarUInt32();
                if (reference != 0) ThrowInvalidReference(reference);
                return null;
            }

            // Read the non-null value.
            return _fieldCodec.ReadValue(ref reader, field);
        }

        private void ThrowInvalidReference(uint reference) => throw new ReferenceNotFoundException(typeof(T?), reference);
    }

    /// <summary>
    /// Copier for <see cref="Nullable{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterCopier]
    public sealed class NullableCopier<T> : IDeepCopier<T?>, IOptionalDeepCopier where T : struct
    {
        private readonly IDeepCopier<T> _copier;

        /// <summary>
        /// Initializes a new instance of the <see cref="NullableCopier{T}"/> class.
        /// </summary>
        /// <param name="copier">The copier.</param>
        public NullableCopier(IDeepCopier<T> copier) => _copier = OrleansGeneratedCodeHelper.GetOptionalCopier(copier);

        public bool IsShallowCopyable() => _copier is null;

        object IDeepCopier.DeepCopy(object input, CopyContext context) => input is null || _copier is null ? input : _copier.DeepCopy(input, context);

        /// <inheritdoc/>
        public T? DeepCopy(T? input, CopyContext context) => input is null || _copier is null ? input : _copier.DeepCopy(input.GetValueOrDefault(), context);
    }
}
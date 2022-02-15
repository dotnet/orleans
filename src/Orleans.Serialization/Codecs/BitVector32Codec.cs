using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Collections.Specialized;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="BitVector32"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class BitVector32Codec : IFieldCodec<BitVector32>
    {
        /// <inheritdoc />
        void IFieldCodec<BitVector32>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, BitVector32 value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, BitVector32 value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(BitVector32), WireType.Fixed32);
            writer.WriteInt32(value.Data);  // BitVector32.Data gets the value of the BitVector32 as an Int32
        }

        /// <inheritdoc/>
        BitVector32 IFieldCodec<BitVector32>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        public static BitVector32 ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            if (field.WireType != WireType.Fixed32)
            {
                ThrowUnsupportedWireTypeException(field);
            }

            return new BitVector32(reader.ReadInt32());
        }

        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.Fixed32} is supported for {nameof(BitVector32)} fields. {field}");
    }

    /// <summary>
    /// Copier for <see cref="BitVector32"/>.
    /// </summary>
    [RegisterCopier]
    public sealed class BitVector32Copier : IDeepCopier<BitVector32>
    {
        /// <inheritdoc/>
        public BitVector32 DeepCopy(BitVector32 input, CopyContext _) => new(input);
    }
}
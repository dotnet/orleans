using System;
using System.Buffers;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="IPAddress"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class IPAddressCodec : IFieldCodec<IPAddress>, IDerivedTypeCodec
    {
        /// <summary>
        /// The codec field type
        /// </summary>
        public static readonly Type CodecFieldType = typeof(IPAddress);

        /// <inheritdoc/>
        IPAddress IFieldCodec<IPAddress>.ReadValue<TInput>(ref Buffers.Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        public static IPAddress ReadValue<TInput>(ref Buffers.Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return (IPAddress)ReferenceCodec.ReadReference(ref reader, field, CodecFieldType);
            }

            var length = reader.ReadVarUInt32();
            IPAddress result;
#if NET5_0_OR_GREATER
            if (reader.TryReadBytes((int)length, out var bytes))
            {
                result = new IPAddress(bytes);
            }
            else
            {
#endif
                var addressBytes = reader.ReadBytes(length);
                result = new IPAddress(addressBytes);
#if NET5_0_OR_GREATER
            }
#endif

            ReferenceCodec.RecordObject(reader.Session, result);
            return result;
        }

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        void IFieldCodec<IPAddress>.WriteField<TBufferWriter>(ref Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, IPAddress value)
        {
            WriteField(ref writer, fieldIdDelta, expectedType, value);
        }

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, IPAddress value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.LengthPrefixed);

            Unsafe.SkipInit(out Guid tmp); // workaround for C#10 limitation around ref scoping (C#11 will add scoped ref parameters)
            var buffer = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref tmp, 1));
            if (!value.TryWriteBytes(buffer, out var length)) throw new NotSupportedException();
            buffer = buffer[..length];

            writer.WriteVarUInt32((uint)buffer.Length);
            writer.Write(buffer);
        }
    }

    /// <summary>
    /// Copier for <see cref="IPAddress"/>.
    /// </summary>
    [RegisterCopier]
    public sealed class IPAddressCopier : IDeepCopier<IPAddress>
    {
        /// <inheritdoc/>
        public IPAddress DeepCopy(IPAddress input, CopyContext _) => input;
    }
}

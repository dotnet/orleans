using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="object"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class ObjectCodec : IFieldCodec<object>
    {
        private static readonly Type ObjectType = typeof(object);

        /// <inheritdoc/>
        object IFieldCodec<object>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        public static object ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.IsReference)
            {
                return ReferenceCodec.ReadReference(ref reader, field.FieldType ?? ObjectType);
            }

            if (field.FieldType is null || field.FieldType == ObjectType)
                return ReadObject(ref reader, field);

            var specificSerializer = reader.Session.CodecProvider.GetCodec(field.FieldType);
            return specificSerializer.ReadValue(ref reader, field);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object ReadObject<TInput>(ref Reader<TInput> reader, Field field)
        {
            field.EnsureWireType(WireType.LengthPrefixed);

            var length = reader.ReadVarUInt32();
            if (length != 0) throw new UnexpectedLengthPrefixValueException(nameof(Object), 0, length);

            var result = new object();
            ReferenceCodec.RecordObject(reader.Session, result);
            return result;
        }

        /// <inheritdoc/>
        void IFieldCodec<object>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value is null)
            {
                ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
                return;
            }

            var specificSerializer = writer.Session.CodecProvider.GetCodec(value.GetType());
            specificSerializer.WriteField(ref writer, fieldIdDelta, expectedType, value);
        }

        void IFieldCodec.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value)
        {
            // only the untyped writer will need to support actual object type values
            if (value is null || value.GetType() == typeof(object))
            {
                if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
                {
                    return;
                }

                writer.WriteFieldHeader(fieldIdDelta, expectedType, ObjectType, WireType.LengthPrefixed);
                writer.WriteVarUInt32(0U);
                return;
            }

            var specificSerializer = writer.Session.CodecProvider.GetCodec(value.GetType());
            specificSerializer.WriteField(ref writer, fieldIdDelta, expectedType, value);
        }
    }

    /// <summary>
    /// Copier for <see cref="object"/>.
    /// </summary>
    [RegisterCopier]
    public sealed class ObjectCopier : IDeepCopier<object>
    {
        /// <summary>
        /// Creates a deep copy of the provided input.
        /// </summary>
        /// <param name="input">The input.</param>
        /// <param name="context">The context.</param>
        /// <returns>A copy of <paramref name="input" />.</returns>
        public static object DeepCopy(object input, CopyContext context)
        {
            return context.TryGetCopy<object>(input, out var result) ? result
                : input.GetType() == typeof(object) ? input : context.DeepCopy(input);
        }

        object IDeepCopier<object>.DeepCopy(object input, CopyContext context)
        {
            return context.TryGetCopy<object>(input, out var result) ? result
                : input.GetType() == typeof(object) ? input : context.DeepCopy(input);
        }

        object IDeepCopier.DeepCopy(object input, CopyContext context)
            => input is null || input.GetType() == typeof(object) ? input : context.DeepCopy(input);
    }
}
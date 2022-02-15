using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;

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
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<object, TInput>(ref reader, field);
            }

            if (field.FieldType == ObjectType || field.FieldType is null)
            {
                _ = reader.ReadVarUInt32();
                var result = new object();
                ReferenceCodec.RecordObject(reader.Session, result);
                return result;
            }

            var specificSerializer = reader.Session.CodecProvider.GetCodec(field.FieldType);
            return specificSerializer.ReadValue(ref reader, field);
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
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value) where TBufferWriter : IBufferWriter<byte>
        {
            var fieldType = value?.GetType();
            if (fieldType is null || fieldType == ObjectType)
            {
                if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
                {
                    return;
                }

                writer.WriteFieldHeader(fieldIdDelta, expectedType, ObjectType, WireType.LengthPrefixed);
                writer.WriteVarUInt32(0U);
                return;
            }

            var specificSerializer = writer.Session.CodecProvider.GetCodec(fieldType);
            specificSerializer.WriteField(ref writer, fieldIdDelta, expectedType, value);
        }
    }

    /// <summary>
    /// Copier for <see cref="object"/>.
    /// </summary>
    [RegisterCopier]
    public sealed class ObjectCopier : IDeepCopier<object>
    {
        /// <inheritdoc/>
        public object DeepCopy(object input, CopyContext context)
        {
            if (context.TryGetCopy<object>(input, out var result))
            {
                return result;
            }

            var type = input.GetType();
            if (type != typeof(object))
            {
                return context.Copy(input);
            }

            result = new object();
            context.RecordCopy(input, result);
            return result;
        }
    }
}
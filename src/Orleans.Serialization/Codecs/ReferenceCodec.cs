using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Functionality for reading and writing object references.
    /// </summary>
    public static class ReferenceCodec
    {
        /// <summary>
        /// Indicates that the field being serialized or deserialized is a value type.
        /// </summary>
        /// <param name="session">The serializer session.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MarkValueField(SerializerSession session) => session.ReferencedObjects.MarkValueField();

        /// <summary>
        /// Write an object reference if <paramref name="value"/> has already been written.
        /// This overload is suitable only for static codecs where expected type is statically known.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryWriteReferenceFieldExpected<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldId, object value) where TBufferWriter : IBufferWriter<byte>
        {
            if (!writer.Session.ReferencedObjects.GetOrAddReference(value, out var reference))
            {
                return false;
            }

            writer.WriteFieldHeaderExpected(fieldId, WireType.Reference);
            writer.WriteVarUInt32(reference);
            return true;
        }

        /// <summary>
        /// Write an object reference if <paramref name="value"/> has already been written and has been tracked via <see cref="RecordObject(SerializerSession, object)"/>.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldId">The field identifier.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        /// <returns><see langword="true" /> if a reference was written, <see langword="false" /> otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteReferenceField<TBufferWriter>(
            ref Writer<TBufferWriter> writer,
            uint fieldId,
            Type expectedType,
            object value) where TBufferWriter : IBufferWriter<byte>
        {
            if (!writer.Session.ReferencedObjects.GetOrAddReference(value, out var reference))
            {
                return false;
            }

            writer.WriteFieldHeader(fieldId, expectedType, value?.GetType(), WireType.Reference);
            writer.WriteVarUInt32(reference);
            return true;
        }

        /// <summary>
        /// Write an object reference if <paramref name="value"/> has already been written and has been tracked via <see cref="RecordObject(SerializerSession, object)"/>.        /// 
        /// </summary>
        /// <remarks>This overload allows specifying a fixed reference type for codecs that implement <see cref="IDerivedTypeCodec"/>.</remarks>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldId">The field identifier.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="actualType">The actual type.</param>
        /// <param name="value">The value.</param>
        /// <returns><see langword="true" /> if a reference was written, <see langword="false" /> otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteReferenceField<TBufferWriter>(
            ref Writer<TBufferWriter> writer,
            uint fieldId,
            Type expectedType,
            Type actualType,
            object value) where TBufferWriter : IBufferWriter<byte>
        {
            if (!writer.Session.ReferencedObjects.GetOrAddReference(value, out var reference))
            {
                return false;
            }

            writer.WriteFieldHeader(fieldId, expectedType, value is null ? null : actualType, WireType.Reference);
            writer.WriteVarUInt32(reference);
            return true;
        }

        /// <summary>
        /// Writes the null reference.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldId">The field identifier.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void WriteNullReference<TBufferWriter>(
            ref Writer<TBufferWriter> writer,
            uint fieldId) where TBufferWriter : IBufferWriter<byte>
        {
            writer.Session.ReferencedObjects.MarkValueField();
            writer.WriteFieldHeaderExpected(fieldId, WireType.Reference);
            writer.WriteByte(1); // writer.WriteVarUInt32(0U);
        }

        /// <summary>
        /// Reads a referenced value.
        /// </summary>
        /// <typeparam name="T">The type of the referenced object.</typeparam>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The referenced value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T ReadReference<T, TInput>(ref Reader<TInput> reader, Field field) => (T)ReadReference(ref reader, field.FieldType ?? typeof(T));

        /// <summary>
        /// Reads the reference.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="fieldType">The field type.</param>
        /// <returns>The referenced value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static object ReadReference<TInput>(ref Reader<TInput> reader, Type fieldType)
        {
            MarkValueField(reader.Session);
            var reference = reader.ReadVarUInt32();
            if (reference == 0)
                return null;

            return ReadReference(ref reader, fieldType, reference);
        }

        private static object ReadReference<TInput>(ref Reader<TInput> reader, Type fieldType, uint reference)
        {
            var value = reader.Session.ReferencedObjects.TryGetReferencedObject(reference);
            if (value is null) throw new ReferenceNotFoundException(fieldType, reference);

            return value switch
            {
                UnknownFieldMarker marker => DeserializeFromMarker(ref reader, fieldType, marker, reference),
                _ => value,
            };
        }

        private static object DeserializeFromMarker<TInput>(
            ref Reader<TInput> reader,
            Type fieldType,
            UnknownFieldMarker marker,
            uint reference)
        {
            // Capture state from the reader and session.
            var session = reader.Session;
            var originalPosition = reader.Position;
            var referencedObjects = session.ReferencedObjects;
            var originalCurrentReferenceId = referencedObjects.CurrentReferenceId;
            var originalReferenceToObjectCount = referencedObjects.ReferenceToObjectCount;

            // Deserialize the object, replacing the marker in the session.
            try
            {
                // Create a reader at the position specified by the marker.
                reader.ForkFrom(marker.Position, out var referencedReader);

                // Determine the correct type for the field.
                fieldType = marker.Field.FieldType ?? fieldType;

                // Get a serializer for that type.
                var specificSerializer = session.CodecProvider.GetCodec(fieldType);

                // Reset the session's reference id so that the deserialized objects overwrite the placeholder markers.
                referencedObjects.CurrentReferenceId = reference - 1;
                referencedObjects.ReferenceToObjectCount = referencedObjects.GetReferenceIndex(marker);
                return specificSerializer.ReadValue(ref referencedReader, marker.Field);
            }
            finally
            {
                // Revert the reference id.
                referencedObjects.CurrentReferenceId = originalCurrentReferenceId;
                referencedObjects.ReferenceToObjectCount = originalReferenceToObjectCount;
                reader.ResumeFrom(originalPosition);
            }
        }

        /// <summary>
        /// Records that an object was read or written.
        /// </summary>
        /// <param name="session">The session.</param>
        /// <param name="value">The value.</param>
        public static void RecordObject(SerializerSession session, object value) => session.ReferencedObjects.RecordReferenceField(value);

        /// <summary>
        /// Records that an object was read or written.
        /// </summary>
        /// <param name="session">The session.</param>
        /// <param name="value">The value.</param>
        /// <param name="referenceId">The reference identifier.</param>
        public static void RecordObject(SerializerSession session, object value, uint referenceId) => session.ReferencedObjects.RecordReferenceField(value, referenceId);

        /// <summary>
        /// Records and returns a placeholder reference id for objects which cannot be immediately deserialized.
        /// </summary>
        /// <param name="session">The session.</param>
        /// <returns>The placeholder reference id.</returns>
        public static uint CreateRecordPlaceholder(SerializerSession session) => session.ReferencedObjects.CreateRecordPlaceholder();
    }
}
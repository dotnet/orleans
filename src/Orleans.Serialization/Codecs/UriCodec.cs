using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="Uri"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class UriCodec : IFieldCodec<Uri>, IDerivedTypeCodec
    {
        Uri IFieldCodec<Uri>.ReadValue<TInput>(ref Buffers.Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        public static Uri ReadValue<TInput>(ref Buffers.Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
                return ReferenceCodec.ReadReference<Uri, TInput>(ref reader, field);

            field.EnsureWireTypeTagDelimited();
            var referencePlaceholder = ReferenceCodec.CreateRecordPlaceholder(reader.Session);

            reader.ReadFieldHeader(ref field);
            if (!field.HasFieldId || field.FieldIdDelta != 0) throw new RequiredFieldMissingException("Serialized Uri is missing its value.");
            var uriString = StringCodec.ReadValue(ref reader, field);
            reader.ReadFieldHeader(ref field);
            reader.ConsumeEndBaseOrEndObject(ref field);

            var result = new Uri(uriString, UriKind.RelativeOrAbsolute);
            ReferenceCodec.RecordObject(reader.Session, result, referencePlaceholder);
            return result;
        }

        void IFieldCodec<Uri>.WriteField<TBufferWriter>(ref Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Uri value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, typeof(Uri), value))
                return;

            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(Uri), WireType.TagDelimited);
            StringCodec.WriteField(ref writer, 0, value.OriginalString);
            writer.WriteEndObject();
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, Uri value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceFieldExpected(ref writer, fieldIdDelta, value))
                return;

            writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.TagDelimited);
            StringCodec.WriteField(ref writer, 0, value.OriginalString);
            writer.WriteEndObject();
        }
    }

    [RegisterCopier]
    internal sealed class UriCopier : ShallowCopier<Uri>, IDerivedTypeCopier { }
}
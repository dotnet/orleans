using System;
using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Serializers
{
    /// <summary>
    /// Serializer for types which are abstract and therefore cannot be instantiated themselves, such as abstract classes and interface types.
    /// </summary>
    /// <typeparam name="TField">The field type.</typeparam>
    public class AbstractTypeSerializer<TField> : AbstractTypeSerializer, IFieldCodec<TField>, IBaseCodec<TField> where TField : class
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AbstractTypeSerializer{TField}"/> class.
        /// </summary>
        protected AbstractTypeSerializer() : base(typeof(TField)) { }

        /// <inheritdoc/>
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, [System.Diagnostics.CodeAnalysis.AllowNull] Type expectedType, [System.Diagnostics.CodeAnalysis.AllowNull] TField value) where TBufferWriter : IBufferWriter<byte>
            => base.WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <inheritdoc/>
        [return: System.Diagnostics.CodeAnalysis.MaybeNull]
        public new TField ReadValue<TInput>(ref Reader<TInput> reader, Field field) => (TField)base.ReadValue(ref reader, field)!;

        /// <inheritdoc/>
        public virtual void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, TField instance) where TBufferWriter : IBufferWriter<byte> { }

        /// <inheritdoc/>
        public virtual void Deserialize<TReaderInput>(ref Reader<TReaderInput> reader, TField instance) => reader.ConsumeEndBaseOrEndObject();
    }

    // without the class type constraint
    internal sealed class AbstractTypeSerializerWrapper<TField> : AbstractTypeSerializer, IFieldCodec<TField>
    {
        public AbstractTypeSerializerWrapper() : base(typeof(TField)) { }

        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, [System.Diagnostics.CodeAnalysis.AllowNull] Type expectedType, [System.Diagnostics.CodeAnalysis.AllowNull] TField value) where TBufferWriter : IBufferWriter<byte>
            => base.WriteField(ref writer, fieldIdDelta, expectedType, value);

        [return: System.Diagnostics.CodeAnalysis.MaybeNull]
        public new TField ReadValue<TInput>(ref Reader<TInput> reader, Field field) => (TField)base.ReadValue(ref reader, field)!;
    }

    /// <summary>
    /// Serializes values whose declared type cannot be instantiated directly, such as abstract classes and interfaces.
    /// </summary>
    public class AbstractTypeSerializer : IFieldCodec
    {
        private readonly Type _fieldType;

        /// <summary>
        /// Initializes a new instance of the <see cref="AbstractTypeSerializer"/> class.
        /// </summary>
        /// <param name="fieldType">The declared field type.</param>
        protected internal AbstractTypeSerializer(Type fieldType) => _fieldType = fieldType;

        /// <inheritdoc/>
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, [System.Diagnostics.CodeAnalysis.AllowNull] Type expectedType, [System.Diagnostics.CodeAnalysis.AllowNull] object? value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value is null)
            {
                ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
                return;
            }

            var specificSerializer = writer.Session.CodecProvider.GetCodec(value.GetType());
            specificSerializer.WriteField(ref writer, fieldIdDelta, expectedType, value);
        }

        /// <inheritdoc/>
        [return: System.Diagnostics.CodeAnalysis.MaybeNull]
        public object? ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference(ref reader, field.FieldType ?? _fieldType);

            var fieldType = field.FieldType;
            if (fieldType is null)
                ThrowMissingFieldType();

            var specificSerializer = reader.Session.CodecProvider.GetCodec(fieldType!);
            return specificSerializer.ReadValue(ref reader, field);
        }

        private void ThrowMissingFieldType() => throw new FieldTypeMissingException(_fieldType);
    }
}

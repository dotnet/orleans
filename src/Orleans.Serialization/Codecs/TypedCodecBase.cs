using Orleans.Serialization.Buffers;
using Orleans.Serialization.WireProtocol;
using System;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Base type for typed codecs.
    /// </summary>
    /// <typeparam name="TField">The field type.</typeparam>
    public abstract class TypedCodecBase<TField> : IFieldCodec<object>
    {
        private readonly IFieldCodec<TField> _codec;

        /// <summary>
        /// Initializes a new instance of the <see cref="TypedCodecBase{TField}"/> class.
        /// </summary>
        protected TypedCodecBase()
        {
            _codec = this as IFieldCodec<TField>;
            if (_codec is null)
            {
                ThrowInvalidSubclass();
            }
        }

        /// <inheritdoc />
        void IFieldCodec<object>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value) => _codec.WriteField(ref writer, fieldIdDelta, expectedType, (TField)value);

        /// <inheritdoc />
        object IFieldCodec<object>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => _codec.ReadValue(ref reader, field);

        private static void ThrowInvalidSubclass() => throw new InvalidCastException($"Subclasses of {typeof(TypedCodecBase<TField>)} must implement/derive from {typeof(IFieldCodec<TField>)}.");
    }
}
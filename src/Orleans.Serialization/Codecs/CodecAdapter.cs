using Orleans.Serialization.Buffers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Methods for adapting typed and untyped codecs
    /// </summary>
    internal static class CodecAdapter
    {
        /// <summary>
        /// Converts a strongly-typed codec into an untyped codec.
        /// </summary>
        /// <typeparam name="TField">The underlying field type.</typeparam>
        /// <typeparam name="TCodec">The type of the codec being adapted.</typeparam>
        /// <param name="typedCodec">The typed codec.</param>
        /// <returns>The adapted codec.</returns>
        public static IFieldCodec<object> CreateUntypedFromTyped<TField, TCodec>(TCodec typedCodec) where TCodec : IFieldCodec<TField> => new TypedCodecWrapper<TField, TCodec>(typedCodec);

        /// <summary>
        /// Converts an untyped codec into a strongly-typed codec.
        /// </summary>
        /// <typeparam name="TField">The underlying field type.</typeparam>
        /// <param name="untypedCodec">The untyped codec.</param>
        /// <returns>The adapted coded.</returns>
        public static IFieldCodec<TField> CreateTypedFromUntyped<TField>(IFieldCodec<object> untypedCodec) => new UntypedCodecWrapper<TField>(untypedCodec);

        private sealed class TypedCodecWrapper<TField, TCodec> : IFieldCodec<object>, IWrappedCodec where TCodec : IFieldCodec<TField>
        {
            private readonly TCodec _codec;

            public TypedCodecWrapper(TCodec codec)
            {
                _codec = codec;
            }

            void IFieldCodec<object>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value) => _codec.WriteField(ref writer, fieldIdDelta, expectedType, (TField)value);

            object IFieldCodec<object>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => _codec.ReadValue(ref reader, field);

            public object Inner => _codec;
        }

        private sealed class UntypedCodecWrapper<TField> : IWrappedCodec, IFieldCodec<TField>
        {
            private readonly IFieldCodec<object> _codec;

            public UntypedCodecWrapper(IFieldCodec<object> codec)
            {
                _codec = codec;
            }

            public object Inner => _codec;

            void IFieldCodec<TField>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TField value) => _codec.WriteField(ref writer, fieldIdDelta, expectedType, value);

            TField IFieldCodec<TField>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => (TField)_codec.ReadValue(ref reader, field);
        }
    }

    /// <summary>
    /// Methods for adapting typed and untyped base type codecs
    /// </summary>
    internal static class BaseCodecAdapter
    {
        /// <summary>
        /// Converts a strongly-typed codec into an untyped base codec.
        /// </summary>
        /// <typeparam name="TField">The field type.</typeparam>
        /// <typeparam name="TCodec">The codec type.</typeparam>
        /// <param name="typedCodec">The typed codec.</param>
        /// <returns>The adapted codec.</returns>
        public static IBaseCodec<object> CreateUntypedFromTyped<TField, TCodec>(TCodec typedCodec) where TCodec : IBaseCodec<TField> where TField : class => new TypedBaseCodecWrapper<TField, TCodec>(typedCodec);

        /// <summary>
        /// Converts an untyped codec into a strongly-typed base codec.
        /// </summary>
        /// <typeparam name="TField">The field type.</typeparam>
        /// <param name="untypedCodec">The untyped codec.</param>
        /// <returns>The adapted codec.</returns>
        public static IBaseCodec<TField> CreateTypedFromUntyped<TField>(IBaseCodec<object> untypedCodec) where TField : class => new UntypedBaseCodecWrapper<TField>(untypedCodec);

        private sealed class TypedBaseCodecWrapper<TField, TCodec> : IBaseCodec<object>, IWrappedCodec where TCodec : IBaseCodec<TField> where TField : class
        {
            private readonly TCodec _codec;

            /// <summary>
            /// Initializes a new instance of the <see cref="TypedBaseCodecWrapper{TField, TCodec}"/> class.
            /// </summary>
            /// <param name="codec">The codec.</param>
            public TypedBaseCodecWrapper(TCodec codec)
            {
                _codec = codec;
            }

            /// <inheritdoc />
            public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, object value) where TBufferWriter : IBufferWriter<byte> => _codec.Serialize(ref writer, (TField)value);

            /// <inheritdoc />
            public void Deserialize<TInput>(ref Reader<TInput> reader, object value) => _codec.Deserialize(ref reader, (TField)value);

            /// <inheritdoc />
            public object Inner => _codec;
        }

        private sealed class UntypedBaseCodecWrapper<TField> : IWrappedCodec, IBaseCodec<TField> where TField : class
        {
            private readonly IBaseCodec<object> _codec;

            /// <summary>
            /// Initializes a new instance of the <see cref="UntypedBaseCodecWrapper{TField}"/> class.
            /// </summary>
            /// <param name="codec">The codec.</param>
            public UntypedBaseCodecWrapper(IBaseCodec<object> codec)
            {
                _codec = codec;
            }

            /// <inheritdoc />
            public object Inner => _codec;

            /// <inheritdoc />
            public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, TField value) where TBufferWriter : IBufferWriter<byte> => _codec.Serialize(ref writer, value);

            /// <inheritdoc />
            public void Deserialize<TInput>(ref Reader<TInput> reader, TField value) => _codec.Deserialize(ref reader, value);
        }
    }
}
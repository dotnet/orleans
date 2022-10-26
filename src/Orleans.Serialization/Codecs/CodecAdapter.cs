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
        /// <param name="typedCodec">The typed codec.</param>
        /// <returns>The adapted codec.</returns>
        public static IFieldCodec<object> CreateUntypedFromTyped<TField>(IFieldCodec<TField> typedCodec) => new TypedCodecWrapper<TField>(typedCodec);

        /// <summary>
        /// Converts an untyped codec into a strongly-typed codec.
        /// </summary>
        /// <typeparam name="TField">The underlying field type.</typeparam>
        /// <param name="untypedCodec">The untyped codec.</param>
        /// <returns>The adapted coded.</returns>
        public static IFieldCodec<TField> CreateTypedFromUntyped<TField>(IFieldCodec<object> untypedCodec) => new UntypedCodecWrapper<TField>(untypedCodec);

        private sealed class TypedCodecWrapper<TField> : IFieldCodec<object>, IWrappedCodec
        {
            private readonly IFieldCodec<TField> _codec;

            public TypedCodecWrapper(IFieldCodec<TField> codec)
            {
                _codec = codec;
            }

            public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value) where TBufferWriter : IBufferWriter<byte>
                => _codec.WriteField(ref writer, fieldIdDelta, expectedType, (TField)value);

            public object ReadValue<TInput>(ref Reader<TInput> reader, Field field) => _codec.ReadValue(ref reader, field);

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

            public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TField value) where TBufferWriter : IBufferWriter<byte>
                => _codec.WriteField(ref writer, fieldIdDelta, expectedType, value);

            public TField ReadValue<TInput>(ref Reader<TInput> reader, Field field) => (TField)_codec.ReadValue(ref reader, field);
        }
    }
}
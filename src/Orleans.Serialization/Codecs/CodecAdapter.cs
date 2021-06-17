using Orleans.Serialization.Buffers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;
using System;

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
        public static IFieldCodec<object> CreateUntypedFromTyped<TField, TCodec>(TCodec typedCodec) where TCodec : IFieldCodec<TField> => new TypedCodecWrapper<TField, TCodec>(typedCodec);

        /// <summary>
        /// Converts an untyped codec into a strongly-typed codec.
        /// </summary>
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
}
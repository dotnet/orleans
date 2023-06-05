using Orleans.Serialization.Buffers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Marker type for field codecs.
    /// </summary>
    public interface IFieldCodec
    {
        /// <summary>
        /// Writes a field using the provided untyped value. The type must still match the codec instance!
        /// </summary>
        void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value) where TBufferWriter : IBufferWriter<byte>;

        /// <summary>
        /// Reads a value and returns it untyped. The type must still match the codec instance!
        /// </summary>
        object ReadValue<TInput>(ref Reader<TInput> reader, Field field);
    }

    /// <summary>
    /// Provides functionality for reading and writing values of a specified type.
    /// Implements the <see cref="Orleans.Serialization.Codecs.IFieldCodec" />
    /// </summary>
    /// <typeparam name="T">The type which this implementation can read and write.</typeparam>
    /// <seealso cref="Orleans.Serialization.Codecs.IFieldCodec" />
    public interface IFieldCodec<T> : IFieldCodec
    {
        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, T value) where TBufferWriter : IBufferWriter<byte>;

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        new T ReadValue<TInput>(ref Reader<TInput> reader, Field field);

        void IFieldCodec.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value)
            => WriteField(ref writer, fieldIdDelta, expectedType, (T)value);

        object IFieldCodec.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);
    }

    /// <summary>
    /// Marker interface for codecs which directly support serializing all derived types of their specified type.
    /// </summary>
    public interface IDerivedTypeCodec : IFieldCodec
    {
    }

    /// <summary>
    /// Hooks for stages in serialization and copying.
    /// </summary>
    /// <typeparam name="T">The underlying value type.</typeparam>
    public interface ISerializationCallbacks<T>
    {
        /// <summary>
        /// Called when serializing.
        /// </summary>
        /// <param name="value">The value.</param>
        void OnSerializing(T value);

        /// <summary>
        /// Called when a value has been serialized.
        /// </summary>
        /// <param name="value">The value.</param>
        void OnSerialized(T value);

        /// <summary>
        /// Called when deserializing.
        /// </summary>
        /// <param name="value">The value.</param>
        void OnDeserializing(T value);

        /// <summary>
        /// Called when a value has been deserialized.
        /// </summary>
        /// <param name="value">The value.</param>
        void OnDeserialized(T value);

        /// <summary>
        /// Called when copying.
        /// </summary>
        /// <param name="original">The original value.</param>
        /// <param name="result">The copy.</param>
        void OnCopying(T original, T result);

        /// <summary>
        /// Called when a value has been copied.
        /// </summary>
        /// <param name="original">The original value.</param>
        /// <param name="result">The copy.</param>
        void OnCopied(T original, T result);
    }

    internal sealed class UntypedCodecWrapper<TField> : IFieldCodec<TField>
    {
        private readonly IFieldCodec _codec;

        public UntypedCodecWrapper(IFieldCodec codec) => _codec = codec;

        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TField value) where TBufferWriter : IBufferWriter<byte>
            => _codec.WriteField(ref writer, fieldIdDelta, expectedType, value);

        void IFieldCodec.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value)
            => _codec.WriteField(ref writer, fieldIdDelta, expectedType, value);

        public TField ReadValue<TInput>(ref Reader<TInput> reader, Field field) => (TField)_codec.ReadValue(ref reader, field);

        object IFieldCodec.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => _codec.ReadValue(ref reader, field);
    }

}
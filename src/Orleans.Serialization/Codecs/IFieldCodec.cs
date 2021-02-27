using Orleans.Serialization.Buffers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;

namespace Orleans.Serialization.Codecs
{
    public interface IFieldCodec
    {
    }

    public interface IFieldCodec<T> : IFieldCodec
    {
        void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, T value) where TBufferWriter : IBufferWriter<byte>;
        T ReadValue<TInput>(ref Reader<TInput> reader, Field field);
    }

    /// <summary>
    /// Marker interface for codecs which directly support serializing all derived types of their specified type.
    /// </summary>
    public interface IDerivedTypeCodec
    {
    }

    public interface ISerializationCallbacks<T>
    {
        void OnSerializing(T value);
        void OnSerialized(T value);
        void OnDeserializing(T value);
        void OnDeserialized(T value);
        void OnCopying(T original, T result);
        void OnCopied(T original, T result);
    }
}
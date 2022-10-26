using Orleans.Serialization.Buffers;
using System;
using System.Buffers;

namespace Orleans.Serialization.Serializers
{
    /// <summary>
    /// Functionality for serializing and deserializing members in a type hierarchy.
    /// </summary>
    /// <typeparam name="T">The type supported by this codec.</typeparam>
    public interface IBaseCodec<in T> : IBaseCodec where T : class
    {
        /// <summary>
        /// Serializes the provided value.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="value">The value.</param>
        void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, T value) where TBufferWriter : IBufferWriter<byte>;

        /// <summary>
        /// Deserializes into the provided value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="value">The value.</param>
        void Deserialize<TInput>(ref Reader<TInput> reader, T value);
    }

    /// <summary>
    /// Marker interface for base serializers.
    /// </summary>
    public interface IBaseCodec
    {
    }

    /// <summary>
    /// A base type serializer which supports multiple types.
    /// </summary>
    public interface IGeneralizedBaseCodec : IBaseCodec<object>
    {
        /// <summary>
        /// Determines whether the specified type is supported by this instance.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns><see langword="true" /> if the specified type is supported; otherwise, <see langword="false" />.</returns>
        bool IsSupportedType(Type type);
    }

    /// <summary>
    /// Provides functionality for creating <see cref="IBaseCodec"/> instances which support a given type.
    /// </summary>
    public interface ISpecializableBaseCodec
    {
        /// <summary>
        /// Determines whether the specified type is supported by this instance.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns><see langword="true" /> if the specified type is supported; otherwise, <see langword="false" />.</returns>
        bool IsSupportedType(Type type);

        /// <summary>
        /// Gets an <see cref="IBaseCodec"/> implementation which supports the specified type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>An <see cref="IBaseCodec"/> implementation which supports the specified type.</returns>
        IBaseCodec GetSpecializedCodec(Type type);
    }
}
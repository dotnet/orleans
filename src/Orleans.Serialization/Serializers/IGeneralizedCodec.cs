using System;
using Orleans.Serialization.Codecs;

namespace Orleans.Serialization.Serializers
{
    /// <summary>
    /// A codec which supports multiple types.
    /// </summary>
    public interface IGeneralizedCodec : IFieldCodec
    {
        /// <summary>
        /// Determines whether the specified type is supported by this instance.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns><see langword="true" /> if the specified type is supported; otherwise, <see langword="false" />.</returns>
        bool IsSupportedType(Type type);
    }

    /// <summary>
    /// Provides access to codecs for multiple types.
    /// </summary>
    public interface ISpecializableCodec
    {
        /// <summary>
        /// Determines whether the specified type is supported by this instance.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns><see langword="true" /> if the specified type is supported; otherwise, <see langword="false" />.</returns>
        bool IsSupportedType(Type type);

        /// <summary>
        /// Gets an <see cref="IFieldCodec"/> implementation which supports the specified type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>An <see cref="IFieldCodec"/> implementation which supports the specified type.</returns>
        IFieldCodec GetSpecializedCodec(Type type);
    }
}
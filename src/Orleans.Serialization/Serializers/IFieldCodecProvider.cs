using Orleans.Serialization.Codecs;
using System;

namespace Orleans.Serialization.Serializers
{
    /// <summary>
    /// Provides access to field codecs.
    /// </summary>
    public interface IFieldCodecProvider
    {
        /// <summary>
        /// Gets a codec for the specified type.
        /// </summary>
        /// <typeparam name="TField">The field type.</typeparam>
        /// <returns>A codec.</returns>
        IFieldCodec<TField> GetCodec<TField>();

        /// <summary>
        /// Gets a codec for the specific type, or <see langword="null"/> if no appropriate codec was found.
        /// </summary>
        /// <typeparam name="TField">The field type.</typeparam>
        /// <returns>A codec.</returns>
        IFieldCodec<TField> TryGetCodec<TField>();

        /// <summary>
        /// Gets a codec for the specific type.
        /// </summary>
        /// <param name="fieldType">
        /// The field type.
        /// </param>
        /// <returns>A codec.</returns>
        IFieldCodec<object> GetCodec(Type fieldType);

        /// <summary>
        /// Gets a codec for the specific type, or <see langword="null"/> if no appropriate codec was found.
        /// </summary>
        /// <param name="fieldType">
        /// The field type.
        /// </param>
        /// <returns>A codec.</returns>
        IFieldCodec<object> TryGetCodec(Type fieldType);
    }
}
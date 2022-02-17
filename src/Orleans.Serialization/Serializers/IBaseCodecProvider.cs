namespace Orleans.Serialization.Serializers
{
    /// <summary>
    /// Provides access to <see cref="IBaseCodec{T}"/> implementations.
    /// </summary>
    public interface IBaseCodecProvider
    {
        /// <summary>
        /// Gets a base codec for the specified type.
        /// </summary>
        /// <typeparam name="TField">The underlying field type.</typeparam>
        /// <returns>A base codec.</returns>
        IBaseCodec<TField> GetBaseCodec<TField>() where TField : class;
    }
}
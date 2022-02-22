namespace Orleans.Serialization.Serializers
{
    /// <summary>
    /// Provides access to value type serializers.
    /// </summary>
    public interface IValueSerializerProvider
    {
        /// <summary>
        /// Gets the value serializer for the specified type.
        /// </summary>
        /// <typeparam name="TField">The value type.</typeparam>
        /// <returns>A value serializer for the specified type.</returns>
        IValueSerializer<TField> GetValueSerializer<TField>() where TField : struct;
    }
}
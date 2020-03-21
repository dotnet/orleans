namespace Orleans.Serialization
{
    /// <summary>
    /// Interface for serializers which are responsible for serializing type information in addition to object data and can be identified by a numeric id.
    /// </summary>
    internal interface IKeyedSerializer : IExternalSerializer
    {
        /// <summary>
        /// Gets the identifier for this serializer.
        /// </summary>
        KeyedSerializerId SerializerId { get; }
    }
}
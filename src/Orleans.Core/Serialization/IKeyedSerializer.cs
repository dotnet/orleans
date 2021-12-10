using System;

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

        /// <summary>
        /// Returns true if the provided type should be serialized by this serializer.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="isFallback">Whether this is the last chance for this serializer to opt-in to serializing the provided type.</param>
        /// <returns><see langword="true"/> if the provided type should be serialized by this serializer, <see langword="false"/> otherwise.</returns>
        bool IsSupportedType(Type type, bool isFallback);
    }
}
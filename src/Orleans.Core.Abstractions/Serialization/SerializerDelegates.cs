using System;

namespace Orleans.Serialization
{
    /// <summary>
    /// Deserializer function.
    /// </summary>
    /// <param name="expected">Expected Type to receive.</param>
    /// <param name="context">The context under which this object is being deserialized.</param>
    /// <returns>Rehydrated object of the specified Type read from the current position in the input stream.</returns>
    public delegate object Deserializer(Type expected, IDeserializationContext context);

    /// <summary> Serializer function. </summary>
    /// <param name="raw">Input object to be serialized.</param>
    /// <param name="context">The context under which this object is being serialized.</param>
    /// <param name="expected">Current Type active in this stream.</param>
    public delegate void Serializer(object raw, ISerializationContext context, Type expected);

    /// <summary>
    /// Deep copier function.
    /// </summary>
    /// <param name="original">Original object to be deep copied.</param>
    /// <param name="context">The serialization context.</param>
    /// <returns>Deep copy of the original object.</returns>
    public delegate object DeepCopier(object original, ICopyContext context);

    /// <summary>
    /// Contains delegates for serialization.
    /// </summary>
    public struct SerializerMethods
    {
        public SerializerMethods(DeepCopier deepCopy, Serializer serialize, Deserializer deserialize)
        {
            this.DeepCopy = deepCopy;
            this.Serialize = serialize;
            this.Deserialize = deserialize;
        }

        /// <summary>
        /// Gets the deep copier delegate.
        /// </summary>
        public DeepCopier DeepCopy { get; }

        /// <summary>
        /// Gets the serializer delegate.
        /// </summary>
        public Serializer Serialize { get; }

        /// <summary>
        /// Gets the deserializer delegate.
        /// </summary>
        public Deserializer Deserialize { get; }
    }
}

using System;

namespace Orleans.Serialization
{
    /// <summary>
    /// Indicates that a class is to be notified when it has been deserialized.
    /// </summary>
    public interface IOnDeserialized
    {
        /// <summary>
        /// Notifies this instance that it has been fully deserialized.
        /// </summary>
        /// <param name="context">The serializer context.</param>
        void OnDeserialized(DeserializationContext context);
    }

    /// <summary>
    /// Provides services and runtime state associated with a deserialization operation.
    /// </summary>
    public abstract class DeserializationContext
    {
        /// <summary>
        /// Gets the service provider associated with the deserialization operation.
        /// </summary>
        public abstract IServiceProvider ServiceProvider { get; }

        /// <summary>
        /// Gets the Orleans runtime client associated with the deserialization operation.
        /// </summary>
        public abstract object RuntimeClient { get; }
    }
}

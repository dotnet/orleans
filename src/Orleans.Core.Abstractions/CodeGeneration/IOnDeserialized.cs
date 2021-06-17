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

    public abstract class DeserializationContext
    {
        public abstract IServiceProvider ServiceProvider { get; }
        public abstract object RuntimeClient { get; }
    }
}
using System;

namespace Orleans.Serialization
{
    public interface ISerializerContext
    {
        /// <summary>
        /// Gets the serialization manager.
        /// </summary>
        SerializationManager SerializationManager { get; }

        /// <summary>
        /// Gets the service provider.
        /// </summary>
        IServiceProvider ServiceProvider { get; }
        
        /// <summary>
        /// Gets additional context associated with this instance.
        /// </summary>
        object AdditionalContext { get; }
    }
}
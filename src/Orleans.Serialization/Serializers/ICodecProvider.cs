using Orleans.Serialization.Cloning;
using System;

namespace Orleans.Serialization.Serializers
{
    /// <summary>
    /// Provides functionality for accessing codecs, activators, and copiers.
    /// </summary>
    public interface ICodecProvider :
        IFieldCodecProvider,
        IBaseCodecProvider,
        IValueSerializerProvider,
        IActivatorProvider,
        IDeepCopierProvider
    {
        /// <summary>
        /// Gets the service provider.
        /// </summary>
        /// <value>The service provider.</value>
        IServiceProvider Services { get; }
    }
}
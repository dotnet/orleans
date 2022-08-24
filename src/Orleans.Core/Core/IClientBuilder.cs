using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting
{
    /// <summary>
    /// Builder for configuring an Orleans client.
    /// </summary>
    public interface IClientBuilder
    {
        /// <summary>
        /// Gets the services collection.
        /// </summary>
        IServiceCollection Services { get; }
    }
}
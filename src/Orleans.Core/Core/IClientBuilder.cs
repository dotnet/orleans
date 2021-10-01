using Microsoft.Extensions.DependencyInjection;

namespace Orleans
{
    /// <summary>
    /// Builder for configuring an Orleans client.
    /// </summary>
    public interface IClientBuilder
    {
        /// <summary>
        /// The services shared between the client and the host.
        /// </summary>
        IServiceCollection Services { get; }
    }
}
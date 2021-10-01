using System;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting
{
    /// <summary>
    /// Builder for configuring an Orleans server.
    /// </summary>
    public interface ISiloBuilder
    {
        /// <summary>
        /// The services shared by the silo and host.
        /// </summary>
        IServiceCollection Services { get; }
    }
}
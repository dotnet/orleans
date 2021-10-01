using System;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting
{
    /// <summary>
    /// Functionality for building Orleans server instances.
    /// </summary>
    public interface ISiloBuilder
    {
        /// <summary>
        /// The services shared by the silo and host.
        /// </summary>
        IServiceCollection Services { get; }
    }
}
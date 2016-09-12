using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;

namespace Orleans.Runtime.Startup
{
    /// <summary>
    /// A builder for <see cref="SiloHost"/>.
    /// </summary>
    public interface ISiloHostBuilder
    {
        /// <summary>
        /// Builds an <see cref="SiloHost"/> which hosts a silo.
        /// </summary>
        SiloHost Build(string name);

        /// <summary>
        /// Supply the ClusterConfiguration used by the Orleans silo.
        /// </summary>
        /// <param name="clusterConfiguration"></param>
        /// <returns></returns>
        ISiloHostBuilder UseClusterConfiguration(ClusterConfiguration clusterConfiguration);

        /// <summary>
        /// Specify the delegate that is used to configure the services of the web application.
        /// </summary>
        /// <param name="services">The delegate that configures the <see cref="IServiceCollection"/>.</param>
        /// <returns>The <see cref="ISiloHostBuilder"/>.</returns>
        ISiloHostBuilder ConfigureServices(Action<IServiceCollection> services);

        /// <summary>
        /// Specify the delegate that is used to create an IServiceProvider to be used by the Orleans silo.
        /// </summary>
        /// <param name="services">The delegate that can allow a custom IoC container to be used by the Orleans silo.</param>
        /// <returns></returns>
        ISiloHostBuilder BuildServiceProvider(Func<IServiceCollection, IServiceProvider> services);

        /// <summary>
        /// Do some post-creation configuration of the Silo.
        /// </summary>
        /// <param name="silo"></param>
        /// <returns></returns>
        ISiloHostBuilder ConfigureSilo(Action<SiloHost> silo);
    }
}
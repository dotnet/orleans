
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Orleans.Configuration.Overrides
{
    public static class SiloOptionsOverrides
    {
        /// <summary>
        /// Add an override <see cref="ClusterOptions"/> on a per-provider basis.
        /// Note: This is intended for migration purposes as a means to handle previously inconsistent behaviors in how providers used ServiceId and ClusterId.
        /// </summary>
        public static ISiloHostBuilder AddProviderClusterOptions(this ISiloHostBuilder builder, string providerName, Action<OptionsBuilder<ClusterOptions>> configureOptions) => builder.ConfigureServices(services => services.AddOptionsOverride<ClusterOptions>(providerName, configureOptions));

        /// <summary>
        /// Add an override <see cref="ClusterOptions"/> on a per-provider basis.
        /// Note: This is intended for migration purposes as a means to handle previously inconsistent behaviors in how providers used ServiceId and ClusterId.
        /// </summary>
        public static ISiloHostBuilder AddProviderClusterOptions(this ISiloHostBuilder builder, string providerName, Action<ClusterOptions> configureOptions) => builder.ConfigureServices(services => services.AddOptionsOverride<ClusterOptions>(providerName, ob => ob.Configure(configureOptions)));
    }
}


using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration.Overrides
{
    public static class OptionsOverrides
    {
        /// <summary>
        /// Gets <see cref="ClusterOptions"/> which may have been overridden on a per-provider basis.
        /// Note: This is intended for migration purposes as a means to handle previously inconsistent behaviors in how providers used ServiceId and ClusterId.
        /// </summary>
        public static IOptions<ClusterOptions> GetProviderClusterOptions(this IServiceProvider services, string providerName) => services.GetOverridableOption<ClusterOptions>(providerName);

        /// <summary>
        /// Add an override <see cref="ClusterOptions"/> on a per-provider basis.
        /// Note: This is intended for migration purposes as a means to handle previously inconsistent behaviors in how providers used ServiceId and ClusterId.
        /// </summary>
        public static IClientBuilder AddProviderClusterOptions(this IClientBuilder builder, string providerName, Action<OptionsBuilder<ClusterOptions>> configureOptions) => builder.ConfigureServices(services => services.AddOptionsOverride<ClusterOptions>(providerName, configureOptions));

        /// <summary>
        /// Add an override <see cref="ClusterOptions"/> on a per-provider basis.
        /// Note: This is intended for migration purposes as a means to handle previously inconsistent behaviors in how providers used ServiceId and ClusterId.
        /// </summary>
        public static IClientBuilder AddProviderClusterOptions(this IClientBuilder builder, string providerName, Action<ClusterOptions> configureOptions) => builder.ConfigureServices(services => services.AddOptionsOverride<ClusterOptions>(providerName, ob => ob.Configure(configureOptions)));

        /// <summary>
        /// Gets option that can be overriden by named service.
        /// </summary>
        private static IOptions<TOptions> GetOverridableOption<TOptions>(this IServiceProvider services, string key)
            where TOptions : class, new()
        {
            TOptions option = services.GetServiceByName<TOptions>(key);
            return option != null
                ? Options.Create(option)
                : services.GetRequiredService<IOptions<TOptions>>();
        }

        /// <summary>
        /// Add an override for an option via a named service.
        /// </summary>
        internal static IServiceCollection AddOptionsOverride<TOptions>(this IServiceCollection collection, string name, Action<OptionsBuilder<TOptions>> configureOptions)
            where TOptions : class, new()
        {
            configureOptions?.Invoke(collection.AddOptions<TOptions>(name));
            return collection.ConfigureNamedOptionForLogging<TOptions>(name)
                             .AddSingletonNamedService(name, (sp, n) => sp.GetRequiredService<IOptionsSnapshot<TOptions>>().Get(n));
        }
    }
}

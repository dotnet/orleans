
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration.Overrides
{
    /// <summary>
    /// Functionality for overriding options using named options, falling back to the default (unnamed) value.
    /// </summary>
    public static class OptionsOverrides
    {
        /// <summary>
        /// Gets <see cref="ClusterOptions"/> which may have been overridden on a per-provider basis.
        /// Note: This is intended for migration purposes as a means to handle previously inconsistent behaviors in how providers used ServiceId and ClusterId.
        /// </summary>
        public static IOptions<ClusterOptions> GetProviderClusterOptions(this IServiceProvider services, string providerName) => services.GetOverridableOption<ClusterOptions>(providerName);

        /// <summary>
        /// Gets option that can be overridden by named service.
        /// </summary>
        private static IOptions<TOptions> GetOverridableOption<TOptions>(this IServiceProvider services, string key)
            where TOptions : class, new()
        {
            TOptions option = services.GetKeyedService<TOptions>(key);
            return option != null
                ? Options.Create(option)
                : services.GetRequiredService<IOptions<TOptions>>();
        }
    }
}

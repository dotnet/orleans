using System;
using System.Collections.Generic;
using System.Reflection;
using Orleans.Providers;

namespace Orleans.Runtime.Configuration
{
    public static class BootstrapProviderConfiguration
    {
        public const string BOOTSTRAP_PROVIDER_CATEGORY_NAME = "Bootstrap";

        /// <summary>
        /// Registers a given type of <typeparamref name="T"/> where <typeparamref name="T"/> is bootstrap provider
        /// </summary>
        /// <typeparam name="T">Non-abstract type which implements <see cref="IBootstrapProvider"/> interface</typeparam>
        /// <param name="providerName">Name of the bootstrap provider</param>
        /// <param name="properties">Properties that will be passed to bootstrap provider upon initialization</param>
        public static void RegisterBootstrapProvider<T>(this GlobalConfiguration config, string providerName, IDictionary<string, string> properties = null) where T : IBootstrapProvider
        {
            Type providerType = typeof(T);
            var providerTypeInfo = providerType.GetTypeInfo();
            if (providerTypeInfo.IsAbstract ||
                providerTypeInfo.IsGenericType ||
                !typeof(IBootstrapProvider).IsAssignableFrom(providerType))
                throw new ArgumentException("Expected non-generic, non-abstract type which implements IBootstrapProvider interface", "typeof(T)");

            ProviderConfigurationUtility.RegisterProvider(config.ProviderConfigurations, BOOTSTRAP_PROVIDER_CATEGORY_NAME, providerTypeInfo.FullName, providerName, properties);
        }

        /// <summary>
        /// Registers a given bootstrap provider.
        /// </summary>
        /// <param name="providerTypeFullName">Full name of the bootstrap provider type</param>
        /// <param name="providerName">Name of the bootstrap provider</param>
        /// <param name="properties">Properties that will be passed to the bootstrap provider upon initialization </param>
        public static void RegisterBootstrapProvider(this GlobalConfiguration config, string providerTypeFullName, string providerName, IDictionary<string, string> properties = null)
        {
            ProviderConfigurationUtility.RegisterProvider(config.ProviderConfigurations, BOOTSTRAP_PROVIDER_CATEGORY_NAME, providerTypeFullName, providerName, properties);
        }
    }
}

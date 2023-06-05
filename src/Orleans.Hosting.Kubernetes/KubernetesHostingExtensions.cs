using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting.Kubernetes;
using Orleans.Runtime;
using System;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions for hosting a silo in Kubernetes.
    /// </summary>
    public static class KubernetesHostingExtensions
    {
        /// <summary>
        /// Adds Kubernetes hosting support.
        /// </summary>
        public static ISiloBuilder UseKubernetesHosting(this ISiloBuilder siloBuilder) => siloBuilder.ConfigureServices(services => services.UseKubernetesHosting(configureOptions: null));

        /// <summary>
        /// Adds Kubernetes hosting support.
        /// </summary>
        public static ISiloBuilder UseKubernetesHosting(this ISiloBuilder siloBuilder, Action<OptionsBuilder<KubernetesHostingOptions>> configureOptions) => siloBuilder.ConfigureServices(services => services.UseKubernetesHosting(configureOptions));

        /// <summary>
        /// Adds Kubernetes hosting support.
        /// </summary>
        public static IServiceCollection UseKubernetesHosting(this IServiceCollection services) => services.UseKubernetesHosting(configureOptions: null);

        /// <summary>
        /// Adds Kubernetes hosting support.
        /// </summary>
        public static IServiceCollection UseKubernetesHosting(this IServiceCollection services, Action<OptionsBuilder<KubernetesHostingOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<KubernetesHostingOptions>());

            // Configure defaults based on the current environment.
            services.AddSingleton<IConfigureOptions<ClusterOptions>, ConfigureKubernetesHostingOptions>();
            services.AddSingleton<IConfigureOptions<SiloOptions>, ConfigureKubernetesHostingOptions>();
            services.AddSingleton<IPostConfigureOptions<EndpointOptions>, ConfigureKubernetesHostingOptions>();
            services.AddSingleton<IConfigureOptions<KubernetesHostingOptions>, ConfigureKubernetesHostingOptions>();
            services.AddSingleton<IValidateOptions<KubernetesHostingOptions>, KubernetesHostingOptionsValidator>();

            services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, KubernetesClusterAgent>();

            return services;
        }
    }
}

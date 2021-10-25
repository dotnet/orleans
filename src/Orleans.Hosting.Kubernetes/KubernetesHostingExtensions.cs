using k8s;
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
        public static ISiloBuilder UseKubernetesHosting(this ISiloBuilder siloBuilder)
        {
            return siloBuilder.ConfigureServices(services => services.UseKubernetesHosting(configureOptions: null));
        }

        /// <summary>
        /// Adds Kubernetes hosting support.
        /// </summary>
        public static ISiloBuilder UseKubernetesHosting(this ISiloBuilder siloBuilder, Action<OptionsBuilder<KubernetesHostingOptions>> configureOptions)
        {
            return siloBuilder.ConfigureServices(services => services.UseKubernetesHosting(configureOptions));
        }

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

            // Configure the Kubernetes client.
            services.AddHttpClient("Orleans.Kubernetes.Agent")
                .AddTypedClient<IKubernetes>((httpClient, serviceProvider) =>
                {
                    var config = serviceProvider.GetRequiredService<KubernetesHostingOptions>().ClientConfiguration;
                    return new k8s.Kubernetes(
                        config,
                        httpClient);
                }).ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                {
                    var config = serviceProvider.GetRequiredService<KubernetesHostingOptions>().ClientConfiguration;
                    return config.CreateDefaultHttpClientHandler();
                });

            return services;
        }
    }
}

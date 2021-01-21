using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using System;
using System.Net;

namespace Orleans.Hosting.Kubernetes
{
    internal class ConfigureKubernetesHostingOptions :
        IConfigureOptions<ClusterOptions>,
        IConfigureOptions<SiloOptions>,
        IPostConfigureOptions<EndpointOptions>,
        IConfigureOptions<KubernetesHostingOptions>
    {
        private readonly IServiceProvider _serviceProvider;

        public ConfigureKubernetesHostingOptions(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void Configure(KubernetesHostingOptions options)
        {
            options.Namespace = Environment.GetEnvironmentVariable(KubernetesHostingOptions.PodNamespaceEnvironmentVariable);
            options.PodName = Environment.GetEnvironmentVariable(KubernetesHostingOptions.PodNameEnvironmentVariable);
            options.PodIP = Environment.GetEnvironmentVariable(KubernetesHostingOptions.PodIPEnvironmentVariable);
        }

        public void Configure(ClusterOptions options)
        {
            options.ServiceId = Environment.GetEnvironmentVariable(KubernetesHostingOptions.ServiceIdEnvironmentVariable);
            options.ClusterId = Environment.GetEnvironmentVariable(KubernetesHostingOptions.ClusterIdEnvironmentVariable);
        }

        public void Configure(SiloOptions options)
        {
            options.SiloName = Environment.GetEnvironmentVariable(KubernetesHostingOptions.PodNameEnvironmentVariable);
        }

        public void PostConfigure(string name, EndpointOptions options)
        {
            // Use PostConfigure to give the developer an opportunity to set SiloPort and GatewayPort using regular
            // Configure methods without needing to worry about ordering with respect to the UseKubernetesHosting call.
            if (options.AdvertisedIPAddress is null)
            {
                var hostingOptions = _serviceProvider.GetRequiredService<IOptions<KubernetesHostingOptions>>().Value;
                var podIp = IPAddress.Parse(hostingOptions.PodIP);
                options.AdvertisedIPAddress = podIp;
            }

            if (options.SiloListeningEndpoint is null)
            {
                options.SiloListeningEndpoint = new IPEndPoint(IPAddress.Any, options.SiloPort);
            }

            if (options.GatewayListeningEndpoint is null && options.GatewayPort > 0)
            {
                options.GatewayListeningEndpoint = new IPEndPoint(IPAddress.Any, options.GatewayPort);
            }
        }
    }
}

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Fabric;
using System.Fabric.Description;
using System.Net;
using Orleans.ServiceFabric;
using NodeConfiguration = Orleans.Runtime.Configuration.NodeConfiguration;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extension methods for configuring silos hosted on Service Fabric.
    /// </summary>
    public static class ServiceFabricConfigurationExtensions
    {
        /// <summary>
        /// Configures silo and gateway endpoints from the provided Service Fabric service's configuration.
        /// </summary>
        /// <param name="configuration">The node configuration.</param>
        /// <param name="context">The service context.</param>
        /// <returns>The node configuration.</returns>
        public static NodeConfiguration ConfigureServiceFabricSiloEndpoints(this NodeConfiguration configuration, ServiceContext context)
        {
            // Gather configuration from Service Fabric.
            var activation = context.CodePackageActivationContext;
            var endpoints = activation.GetEndpoints();
            var siloEndpoint = GetEndpoint(endpoints, ServiceFabricConstants.SiloEndpointName);
            var gatewayEndpoint = GetEndpoint(endpoints, ServiceFabricConstants.GatewayEndpointName);

            // Set the endpoints according to Service Fabric configuration.
            if (string.IsNullOrWhiteSpace(configuration.HostNameOrIPAddress))
            {
                configuration.HostNameOrIPAddress = context.NodeContext.IPAddressOrFQDN;
            }

            configuration.Port = siloEndpoint.Port;
            configuration.ProxyGatewayEndpoint = new IPEndPoint(configuration.Endpoint.Address, gatewayEndpoint.Port);

            return configuration;
        }

        /// <summary>
        /// Returns the endpoint description with the provided name, throwing an exception if it is not present.
        /// </summary>
        /// <param name="endpoints">The endpoint collection.</param>
        /// <param name="endpointName">the name of the endpoint to return.</param>
        /// <returns>The endpoint with the provided name.</returns>
        /// <exception cref="KeyNotFoundException">The endpoint with the provided name was not found.</exception>
        private static EndpointResourceDescription GetEndpoint(
            KeyedCollection<string, EndpointResourceDescription> endpoints,
            string endpointName)
        {
            if (!endpoints.Contains(endpointName))
            {
                throw new KeyNotFoundException(
                    $"Endpoint \"{endpointName}\" not found in service manifest. Ensure the service has a TCP endpoint with that name.");
            }

            return endpoints[endpointName];
        }
    }
}
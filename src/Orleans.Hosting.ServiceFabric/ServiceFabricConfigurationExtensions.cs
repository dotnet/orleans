using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Fabric;
using System.Fabric.Description;
using Orleans.Configuration;
using Orleans.ServiceFabric;

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
        /// <param name="options">The endpoint configuration.</param>
        /// <param name="context">The service context.</param>
        public static void ConfigureFromServiceContext(this EndpointOptions options, ServiceContext context)
        {
            // Gather configuration from Service Fabric.
            var activation = context.CodePackageActivationContext;
            var endpoints = activation.GetEndpoints();
            var siloEndpoint = GetEndpoint(endpoints, ServiceFabricConstants.SiloEndpointName);
            var gatewayEndpoint = GetEndpoint(endpoints, ServiceFabricConstants.GatewayEndpointName);
            
            options.SiloPort = siloEndpoint.Port;
            options.GatewayPort = gatewayEndpoint.Port;
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
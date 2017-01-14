using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Fabric.Description;
using Orleans.Runtime;

namespace Microsoft.Orleans.ServiceFabric
{
    using System.Fabric;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Text.RegularExpressions;
    using Newtonsoft.Json;
    using global::Orleans.Runtime.Configuration;

    using Microsoft.Orleans.ServiceFabric.Models;
    using Microsoft.Orleans.ServiceFabric.Utilities;
    using Microsoft.ServiceFabric.Services.Communication.Runtime;

    /// <summary>
    /// Service Fabric communication listener which hosts an Orleans silo.
    /// </summary>
    public class OrleansCommunicationListener : ICommunicationListener
    {
        /// <summary>
        /// The name used to identify the silo-to-silo communication endpoint.
        /// </summary>
        public const string SiloEndpointName = "OrleansSiloEndpoint";

        /// <summary>
        /// The name used to identify the client-to-silo communication endpoint.
        /// </summary>
        public const string GatewayEndpointName = "OrleansProxyEndpoint";

        /// <summary>
        /// The Orleans cluster configuration.
        /// </summary>
        private readonly ClusterConfiguration configuration;

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansCommunicationListener" /> class.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="configuration">The configuration.</param>
        public OrleansCommunicationListener(ServiceContext context, ClusterConfiguration configuration)
        {
            this.configuration = configuration;
            if (this.configuration == null)
            {
                this.configuration = new ClusterConfiguration();
                this.configuration.StandardLoad();
            }

            this.SiloName = Regex.Replace(context.ServiceName.PathAndQuery.Trim('/'), "[^a-zA-Z0-9_]", "_") + "_" +
                            context.ReplicaOrInstanceId.ToString("X");

            // Gather configuration from Service Fabric.
            var activation = context.CodePackageActivationContext;
            var endpoints = activation.GetEndpoints();
            var siloEndpoint = GetEndpoint(endpoints, SiloEndpointName);
            var gatewayEndpoint = GetEndpoint(endpoints, GatewayEndpointName);

            // Set the endpoints according to Service Fabric configuration.
            if (string.IsNullOrWhiteSpace(this.configuration.Defaults.HostNameOrIPAddress))
            {
                this.configuration.Defaults.HostNameOrIPAddress = context.NodeContext.IPAddressOrFQDN;
            }

            this.configuration.Defaults.Port = siloEndpoint.Port;
            this.configuration.Defaults.ProxyGatewayEndpoint = new IPEndPoint(this.configuration.Defaults.Endpoint.Address, gatewayEndpoint.Port);
        }

        /// <summary>
        /// Gets the silo name.
        /// </summary>
        public string SiloName { get; }

        /// <summary>
        /// Gets or sets the underlying <see cref="ISiloHost"/>.
        /// </summary>
        /// <remarks>Exposed for testability.</remarks>
        internal ISiloHost SiloHost { get; set; } = new SiloHostWrapper();

        /// <summary>
        /// Starts the silo.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The silo endpoints, represented as a <see langword="string"/>.</returns>
        public Task<string> OpenAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Start the silo.
                if (this.configuration.Defaults.Generation == 0)
                {
                    this.configuration.Defaults.Generation = SiloAddress.AllocateNewGeneration();
                }

                this.SiloHost.Start(this.SiloName, this.configuration);
            }
            catch
            {
                this.Abort();
                throw;
            }

            return Task.FromResult(JsonConvert.SerializeObject(new FabricSiloInfo(this.SiloHost.NodeConfig)));
        }

        /// <summary>
        /// Stops the silo.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        public Task CloseAsync(CancellationToken cancellationToken)
        {
            this.SiloHost.Stop();
            return Task.FromResult(0);
        }

        /// <summary>
        /// Aborts the silo.
        /// </summary>
        public void Abort()
        {
            this.SiloHost.Stop();
            this.SiloHost.Dispose();
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
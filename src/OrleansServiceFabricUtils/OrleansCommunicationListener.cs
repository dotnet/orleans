using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;

namespace Microsoft.Orleans.ServiceFabric
{
    using System.Fabric;

    using global::Orleans.Runtime;

    using Microsoft.ServiceFabric.Services.Communication.Runtime;

    internal class OrleansCommunicationListener : ICommunicationListener
    {
        private readonly ClusterConfiguration configuration;

        private readonly ServiceContext context;

        private readonly IServicePartition partition;

        /// <summary>
        ///     The silo.
        /// </summary>
        private OrleansFabricSilo fabricSilo;
        
        public OrleansCommunicationListener(ServiceContext context, ClusterConfiguration configuration, IServicePartition servicePartition)
        {
            this.context = context;
            this.configuration = configuration;
            this.partition = servicePartition;
        }

        /// <summary>
        /// This method causes the communication listener to be opened. Once the Open
        ///             completes, the communication listener becomes usable - accepts and sends messages.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>
        /// A <see cref="T:System.Threading.Tasks.Task">Task</see> that represents outstanding operation. The result of the Task is
        ///             the endpoint string.
        /// </returns>
        public async Task<string> OpenAsync(CancellationToken cancellationToken)
        {
            var serviceName = this.context.ServiceName;
            var activation = this.context.CodePackageActivationContext;
            var node = await FabricRuntime.GetNodeContextAsync(TimeSpan.FromMinutes(1), cancellationToken);
            var nodeAddress = await GetNodeAddress(node.IPAddressOrFQDN);

            var siloEndpoint = new IPEndPoint(nodeAddress, activation.GetEndpoint("OrleansSiloEndpoint").Port);
            var proxyEndpoint = new IPEndPoint(nodeAddress, activation.GetEndpoint("OrleansProxyEndpoint").Port);
            this.fabricSilo = new OrleansFabricSilo(
                serviceName,
                this.context.ReplicaOrInstanceId,
                siloEndpoint,
                proxyEndpoint,
                this.context.ServiceName.ToString());
            this.MonitorSilo();

            if (!this.fabricSilo.Start(this.configuration))
            {
                throw new OrleansException("Failed to start silo. See log for more detail.");
            }

            return this.fabricSilo.Address.ToParsableString();
        }

        /// <summary>
        /// This method causes the communication listener to close. Close is a terminal state and 
        ///             this method allows the communication listener to transition to this state in a
        ///             graceful manner.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>
        /// A <see cref="T:System.Threading.Tasks.Task">Task</see> that represents outstanding operation.
        /// </returns>
        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            this.fabricSilo?.Stop();
            try
            {
                var stopped = this.fabricSilo?.Stopped;
                if (stopped != null) await stopped;
            }
            finally
            {
                this.fabricSilo?.Abort();
            }
        }

        /// <summary>
        /// This method causes the communication listener to close. Close is a terminal state and
        ///             this method causes the transition to close ungracefully. Any outstanding operations
        ///             (including close) should be canceled when this method is called.
        /// </summary>
        public void Abort() => this.fabricSilo?.Abort();

        /// <summary>
        /// Monitors the current silo, reporting a fault to the current partition if it fails.
        /// </summary>
        private void MonitorSilo()
        {
            this.fabricSilo.Stopped.ContinueWith(
                _ =>
                {
                    if (_.IsFaulted)
                    {
                        this.partition.ReportFault(FaultType.Transient);
                    }
                });
        }

        /// <summary>
        /// Returns the host's network address.
        /// </summary>
        /// <param name="host">
        /// The host.
        /// </param>
        /// <returns>
        /// The host's network address.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Unable to determine the host's network address.
        /// </exception>
        private static async Task<IPAddress> GetNodeAddress(string host)
        {
            var nodeAddresses = await Dns.GetHostAddressesAsync(host);

            var nodeAddressV4 =
                nodeAddresses.FirstOrDefault(_ => _.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocal(_));
            var nodeAddressV6 =
                nodeAddresses.FirstOrDefault(
                    _ => _.AddressFamily == AddressFamily.InterNetworkV6 && !IsLinkLocal(_));
            var nodeAddress = nodeAddressV4 ?? nodeAddressV6;
            if (nodeAddress == null)
            {
                throw new InvalidOperationException("Could not determine own network address.");
            }

            return nodeAddress;
        }

        /// <summary>
        /// Returns <see langword="true"/> if the provided <paramref name="address"/> is a local-only address.
        /// </summary>
        /// <param name="address">
        /// The address.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the provided <paramref name="address"/> is a local-only address.
        /// </returns>
        private static bool IsLinkLocal(IPAddress address)
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return address.IsIPv6LinkLocal;
            }

            // 169.254.0.0/16
            var addrBytes = address.GetAddressBytes();
            return addrBytes[0] == 0xA9 && addrBytes[1] == 0xFE;
        }
    }
}
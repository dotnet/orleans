using System.Collections.Concurrent;
using Orleans;

namespace Microsoft.Orleans.ServiceFabric
{
    using System;
    using System.Collections.Generic;
    using System.Fabric;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using global::Orleans.Messaging;
    using global::Orleans.Runtime;
    using global::Orleans.Runtime.Configuration;

    using Microsoft.Orleans.ServiceFabric.Models;
    using Microsoft.Orleans.ServiceFabric.Utilities;
    using Microsoft.ServiceFabric.Services.Client;

    /// <summary>
    /// Gateway provider which reads gateway information from Service Fabric's naming service.
    /// </summary>
    internal class FabricGatewayProvider : IGatewayListProvider, IGatewayListObservable, IFabricServiceStatusListener, IDisposable
    {
        private readonly ConcurrentDictionary<IGatewayListListener, IGatewayListListener> subscribers =
            new ConcurrentDictionary<IGatewayListListener, IGatewayListListener>();

        private readonly TimeSpan refreshPeriod;

        private FabricServiceSiloResolver fabricServiceSiloResolver;

        private List<Uri> gateways = new List<Uri>();

        private Timer timer;

        private Logger log;

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricGatewayProvider"/> class.
        /// </summary>
        public FabricGatewayProvider()
        {
            this.refreshPeriod = TimeSpan.FromSeconds(30);
            this.MaxStaleness = TimeSpan.FromSeconds(this.refreshPeriod.TotalSeconds * 2);
        }

        /// <summary>
        /// Initializes the provider, will be called before all other methods
        /// </summary>
        /// <param name="clientConfiguration">The client configuration.</param>
        /// <param name="logger">The logger to be used by the provider.</param>
        public async Task InitializeGatewayListProvider(ClientConfiguration clientConfiguration, Logger logger)
        {
            // TODO: inject these
            var serviceName = new Uri(clientConfiguration.DataConnectionString);
            var fabricClient = new FabricClient();
            var queryManager = new FabricQueryManager(fabricClient, new ServicePartitionResolver(() => fabricClient));
            this.fabricServiceSiloResolver = new FabricServiceSiloResolver(serviceName, queryManager, logger.GetLogger);
            this.fabricServiceSiloResolver.Subscribe(this);

            this.log = logger.GetLogger(nameof(FabricGatewayProvider));
            await this.RefreshAsync();
            this.timer = new Timer(this.Refresh, null, this.refreshPeriod, this.refreshPeriod);
        }

        /// <summary>
        /// Returns the list of gateways (silos) that can be used by a client to connect to Orleans cluster.
        /// </summary>
        public Task<IList<Uri>> GetGateways() => Task.FromResult<IList<Uri>>(this.gateways);

        /// <summary>
        /// Specifies how often this IGatewayListProvider is refreshed, to have a bound on max staleness of its returned information.
        /// </summary>
        public TimeSpan MaxStaleness { get; }

        /// <summary>
        /// Specifies whether this IGatewayListProvider ever refreshes its returned information, or always returns the same gw list.
        /// (currently only the static config based StaticGatewayListProvider is not updatable. All others are.)
        /// </summary>
        public bool IsUpdatable => true;

        /// <summary>
        /// Subscribes the provided <paramref name="subscriber"/> from notification events.
        /// </summary>
        /// <param name="subscriber">The listener.</param>
        /// <returns>A value indicating whether the listener was subscribed.</returns>
        public bool SubscribeToGatewayNotificationEvents(IGatewayListListener subscriber)
        {
            this.subscribers.TryAdd(subscriber, subscriber);
            return true;
        }

        /// <summary>
        /// Unsubscribes the provided <paramref name="listener"/> from notification events.
        /// </summary>
        /// <param name="listener">The listener.</param>
        /// <returns>A value indicating whether the listener was unsubscribed.</returns>
        public bool UnSubscribeFromGatewayNotificationEvents(IGatewayListListener listener)
        {
            this.subscribers.TryRemove(listener, out listener);
            return true;
        }

        /// <summary>
        /// Notifies this instance of an update to one or more partitions.
        /// </summary>
        /// <param name="silos">The updated set of partitions.</param>
        public void OnUpdate(FabricSiloInfo[] silos)
        {
            this.gateways = silos.Select(silo => silo.GatewayAddress.ToGatewayUri()).ToList();
            if (this.log.IsVerbose)
            {
                this.log.Verbose($"Updating {this.subscribers.Count} subscribers with {this.gateways.Count} gateways.");
            }

            foreach (var subscriber in this.subscribers.Values)
            {
                try
                {
                    subscriber.GatewayListNotification(this.gateways);
                }
                catch (Exception exception)
                {
                    this.log.Warn(
                        (int) ErrorCode.ServiceFabric_GatewayProvider_ExceptionNotifyingSubscribers,
                        "Exception while notifying subscriber.",
                        exception);
                }
            }
        }

        /// <summary>Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.</summary>
        public void Dispose()
        {
            this.timer?.Dispose();
            this.timer = null;
            this.fabricServiceSiloResolver.Unsubscribe(this);
        }

        /// <summary>
        /// Refreshes the gateway list.
        /// </summary>
        /// <param name="state">The state object.</param>
        private void Refresh(object state)
        {
            this.RefreshAsync().Ignore();
        }

        private async Task RefreshAsync()
        {
            try
            {
                await this.fabricServiceSiloResolver.Refresh();
            }
            catch (Exception exception)
            {
                this.log.Warn(
                    (int) ErrorCode.ServiceFabric_GatewayProvider_ExceptionRefreshingGateways,
                    "Exception while refreshing gateways on scheduled interval",
                    exception);
                throw;
            }
        }
    }
}
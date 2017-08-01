using System.Collections.Concurrent;
using Orleans;

namespace Microsoft.Orleans.ServiceFabric
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using global::Orleans.Messaging;
    using global::Orleans.Runtime;
    using global::Orleans.Runtime.Configuration;

    using Microsoft.Orleans.ServiceFabric.Models;
    using Microsoft.Orleans.ServiceFabric.Utilities;

    /// <summary>
    /// Gateway provider which reads gateway information from Service Fabric's naming service.
    /// </summary>
    internal class FabricGatewayProvider : IGatewayListProvider, IGatewayListObservable, IFabricServiceStatusListener, IDisposable
    {
        private readonly ConcurrentDictionary<IGatewayListListener, IGatewayListListener> subscribers =
            new ConcurrentDictionary<IGatewayListListener, IGatewayListListener>();

        private readonly TimeSpan refreshPeriod;

        private readonly IFabricServiceSiloResolver fabricServiceSiloResolver;

        private List<Uri> gateways = new List<Uri>();

        private Timer timer;

        private Logger log;

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricGatewayProvider"/> class.
        /// </summary>
        /// <param name="siloResolver">The silo resolver.</param>
        public FabricGatewayProvider(IFabricServiceSiloResolver siloResolver)
        {
            this.fabricServiceSiloResolver = siloResolver;
            this.refreshPeriod = TimeSpan.FromSeconds(5);
            this.MaxStaleness = this.refreshPeriod;
        }

        /// <inheritdoc />
        public async Task InitializeGatewayListProvider(ClientConfiguration clientConfiguration, Logger logger)
        {
            this.fabricServiceSiloResolver.Subscribe(this);

            this.log = logger.GetLogger(nameof(FabricGatewayProvider));
            await this.RefreshAsync();
            this.timer = new Timer(this.Refresh, null, this.refreshPeriod, this.refreshPeriod);
        }

        /// <inheritdoc />
        public Task<IList<Uri>> GetGateways() => Task.FromResult<IList<Uri>>(this.gateways);

        /// <inheritdoc />
        public TimeSpan MaxStaleness { get; }

        /// <inheritdoc />
        public bool IsUpdatable => true;

        /// <inheritdoc />
        public bool SubscribeToGatewayNotificationEvents(IGatewayListListener subscriber)
        {
            this.log.Verbose($"Subscribing {subscriber} to gateway notification events.");
            this.subscribers.TryAdd(subscriber, subscriber);
            return true;
        }

        /// <inheritdoc />
        public bool UnSubscribeFromGatewayNotificationEvents(IGatewayListListener subscriber)
        {
            this.log.Verbose($"Unsubscribing {subscriber} from gateway notification events.");
            this.subscribers.TryRemove(subscriber, out subscriber);
            return true;
        }

        /// <inheritdoc />
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

        /// <inheritdoc />
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
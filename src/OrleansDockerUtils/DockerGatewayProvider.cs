using Microsoft.Orleans.Docker.Models;
using Microsoft.Orleans.Docker.Utilities;
using Orleans;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Orleans.Docker
{

    internal class DockerGatewayProvider : IGatewayListProvider, IGatewayListObservable, IDockerStatusListener, IDisposable
    {
        private readonly ConcurrentDictionary<IGatewayListListener, IGatewayListListener> subscribers =
            new ConcurrentDictionary<IGatewayListListener, IGatewayListListener>();
        private readonly TimeSpan refreshPeriod = TimeSpan.FromSeconds(30);
        private IDockerSiloResolver resolver;
        private List<Uri> gateways = new List<Uri>();
        private Timer timer;
        private Logger log;

        /// <summary>
        /// Specifies whether this IGatewayListProvider ever refreshes its returned information, or always returns the same gw list.
        /// (currently only the static config based StaticGatewayListProvider is not updatable. All others are.)
        /// </summary>
        public bool IsUpdatable => true;

        /// <summary>
        /// Specifies how often this IGatewayListProvider is refreshed, to have a bound on max staleness of its returned information.
        /// </summary>
        public TimeSpan MaxStaleness => TimeSpan.FromSeconds(refreshPeriod.TotalSeconds * 2);

        public async Task InitializeGatewayListProvider(ClientConfiguration clientConfiguration, Logger logger)
        {
            resolver = new DockerSiloResolver(
                clientConfiguration.DeploymentId,
                clientConfiguration.CreateDockerClient(),
                logger.GetLogger);

            resolver.Subscribe(this);
            log = logger.GetLogger(nameof(DockerGatewayProvider));
            await RefreshAsync();
            timer = new Timer(Refresh, null, refreshPeriod, refreshPeriod);
        }

        /// <summary>
        /// Returns the list of gateways (silos) that can be used by a client to connect to Orleans cluster.
        /// </summary>
        public Task<IList<Uri>> GetGateways() => Task.FromResult<IList<Uri>>(gateways);

        /// <summary>
        /// Subscribes the provided <paramref name="subscriber"/> from notification events.
        /// </summary>
        /// <param name="subscriber">The listener.</param>
        /// <returns>A value indicating whether the listener was subscribed.</returns>
        public bool SubscribeToGatewayNotificationEvents(IGatewayListListener subscriber)
        {
            subscribers.TryAdd(subscriber, subscriber);
            return true;
        }

        /// <summary>
        /// Unsubscribes the provided <paramref name="listener"/> from notification events.
        /// </summary>
        /// <param name="listener">The listener.</param>
        /// <returns>A value indicating whether the listener was unsubscribed.</returns>
        public bool UnSubscribeFromGatewayNotificationEvents(IGatewayListListener listener)
        {
            subscribers.TryRemove(listener, out listener);
            return true;
        }

        public void Dispose()
        {
            timer?.Dispose();
            timer = null;
            resolver.Unsubscribe(this);
        }

        /// <summary>
        /// Refreshes the gateway list.
        /// </summary>
        /// <param name="state">The state object.</param>
        private void Refresh(object state)
        {
            RefreshAsync().Ignore();
        }

        private async Task RefreshAsync()
        {
            try
            {
                await resolver.Refresh();
            }
            catch (Exception exception)
            {
                log.Warn(
                    (int)ErrorCode.Docker_GatewayProvider_ExceptionRefreshingGateways,
                    "Exception while refreshing gateways on scheduled interval",
                    exception);
                throw;
            }
        }

        public void OnUpdate(DockerSiloInfo[] silos)
        {
            gateways = silos.Where(s => string.IsNullOrWhiteSpace(s.Gateway))
                .Select(silo => silo.GatewayAddress.ToGatewayUri()).ToList();

            if (log.IsVerbose)
            {
                log.Verbose($"Updating {subscribers.Count} subscribers with {gateways.Count} gateways.");
            }

            foreach (var subscriber in subscribers.Values)
            {
                try
                {
                    subscriber.GatewayListNotification(gateways);
                }
                catch (Exception exception)
                {
                    log.Warn(
                        (int)ErrorCode.Docker_GatewayProvider_ExceptionNotifyingSubscribers,
                        "Exception while notifying subscriber.",
                        exception);
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;

namespace Orleans.Messaging
{
    /// <summary>
    /// The GatewayManager class holds the list of known gateways, as well as maintaining the list of "dead" gateways.
    ///
    /// The known list can come from one of two places: the full list may appear in the client configuration object, or
    /// the config object may contain an IGatewayListProvider delegate. If both appear, then the delegate takes priority.
    /// </summary>
    internal class GatewayManager : IDisposable
    {
        private readonly object lockable = new object();
        private readonly Dictionary<SiloAddress, DateTime> knownDead = new Dictionary<SiloAddress, DateTime>();
        private readonly Dictionary<SiloAddress, DateTime> knownMasked = new Dictionary<SiloAddress, DateTime>();
        private readonly IGatewayListProvider gatewayListProvider;
        private readonly ILogger logger;
        private readonly ConnectionManager connectionManager;
        private readonly GatewayOptions gatewayOptions;
        private AsyncTaskSafeTimer gatewayRefreshTimer;
        private List<SiloAddress> cachedLiveGateways;
        private HashSet<SiloAddress> cachedLiveGatewaysSet;
        private List<SiloAddress> knownGateways;
        private DateTime lastRefreshTime;
        private int roundRobinCounter;
        private bool gatewayRefreshCallInitiated;
        private bool gatewayListProviderInitialized;

        public GatewayManager(
            IOptions<GatewayOptions> gatewayOptions,
            IGatewayListProvider gatewayListProvider,
            ILoggerFactory loggerFactory,
            ConnectionManager connectionManager)
        {
            this.gatewayOptions = gatewayOptions.Value;
            this.logger = loggerFactory.CreateLogger<GatewayManager>();
            this.connectionManager = connectionManager;
            this.gatewayListProvider = gatewayListProvider;
            this.gatewayRefreshTimer = new AsyncTaskSafeTimer(
                loggerFactory.CreateLogger<SafeTimer>(),
                RefreshSnapshotLiveGateways_TimerCallback,
                null,
                this.gatewayOptions.GatewayListRefreshPeriod,
                this.gatewayOptions.GatewayListRefreshPeriod);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (!gatewayListProviderInitialized)
            {
                await this.gatewayListProvider.InitializeGatewayListProvider();
                gatewayListProviderInitialized = true;
            }

            var knownGateways = await this.gatewayListProvider.GetGateways();
            if (knownGateways.Count == 0)
            {
                var err = $"Could not find any gateway in {this.gatewayListProvider.GetType().FullName}. Orleans client cannot initialize.";
                this.logger.LogError((int)ErrorCode.GatewayManager_NoGateways, err);
                throw new SiloUnavailableException(err);
            }

            this.logger.LogInformation(
                (int)ErrorCode.GatewayManager_FoundKnownGateways,
                "Found {GatewayCount} gateways: {Gateways}",
                knownGateways.Count,
                Utils.EnumerableToString(knownGateways));

            this.roundRobinCounter = this.gatewayOptions.PreferedGatewayIndex >= 0 ? this.gatewayOptions.PreferedGatewayIndex : ThreadSafeRandom.Next(knownGateways.Count);
            this.knownGateways = this.cachedLiveGateways = knownGateways.Select(gw => gw.ToGatewayAddress()).ToList();
            this.cachedLiveGatewaysSet = new HashSet<SiloAddress>(cachedLiveGateways);
            this.lastRefreshTime = DateTime.UtcNow;
        }

        public void Stop()
        {
            if (gatewayRefreshTimer != null)
            {
                Utils.SafeExecute(gatewayRefreshTimer.Dispose, logger);
            }

            gatewayRefreshTimer = null;
        }

        public void MarkAsDead(SiloAddress gateway)
        {
            lock (lockable)
            {
                knownDead[gateway] = DateTime.UtcNow;
                var copy = new List<SiloAddress>(cachedLiveGateways);
                copy.Remove(gateway);
                // swap the reference, don't mutate cachedLiveGateways, so we can access cachedLiveGateways without the lock.
                cachedLiveGateways = copy;
                cachedLiveGatewaysSet = new HashSet<SiloAddress>(cachedLiveGateways);
            }
        }

        public void MarkAsUnavailableForSend(SiloAddress gateway)
        {
            lock (lockable)
            {
                knownMasked[gateway] = DateTime.UtcNow;
                var copy = new List<SiloAddress>(cachedLiveGateways);
                copy.Remove(gateway);
                // swap the reference, don't mutate cachedLiveGateways, so we can access cachedLiveGateways without the lock.
                cachedLiveGateways = copy;
                cachedLiveGatewaysSet = new HashSet<SiloAddress>(cachedLiveGateways);
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("GatewayManager: ");
            lock (lockable)
            {
                if (cachedLiveGateways != null)
                {
                    sb.Append(cachedLiveGateways.Count);
                    sb.Append(" cachedLiveGateways, ");
                }
                if (knownDead != null)
                {
                    sb.Append(knownDead.Count);
                    sb.Append(" known dead gateways.");
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Selects a gateway to use for a new bucket.
        ///
        /// Note that if a list provider delegate was given, the delegate is invoked every time this method is called.
        /// This method performs caching to avoid hammering the ultimate data source.
        ///
        /// This implementation does a simple round robin selection. It assumes that the gateway list from the provider
        /// is in the same order every time.
        /// </summary>
        /// <returns></returns>
        public SiloAddress GetLiveGateway()
        {
            List<SiloAddress> live = GetLiveGateways();
            int count = live.Count;
            if (count > 0)
            {
                lock (lockable)
                {
                    // Round-robin through the known gateways and take the next live one, starting from where we last left off
                    roundRobinCounter = (roundRobinCounter + 1) % count;
                    return live[roundRobinCounter];
                }
            }
            // If we drop through, then all of the known gateways are presumed dead
            return null;
        }

        public List<SiloAddress> GetLiveGateways()
        {
            // Never takes a lock and returns the cachedLiveGateways list quickly without any operation.
            // Asynchronously starts gateway refresh only when it is empty.
            if (cachedLiveGateways.Count == 0)
            {
                ExpediteUpdateLiveGatewaysSnapshot();

                if (knownGateways.Count > 0)
                {
                    lock (this.lockable)
                    {
                        if (cachedLiveGateways.Count == 0 && knownGateways.Count > 0)
                        {
                            this.logger.LogWarning("All known gateways have been marked dead locally. Expediting gateway refresh and resetting all gateways to live status.");

                            cachedLiveGateways = knownGateways;
                            cachedLiveGatewaysSet = new HashSet<SiloAddress>(knownGateways);
                        }
                    }
                }
            }

            return cachedLiveGateways;
        }

        public bool IsGatewayAvailable(SiloAddress siloAddress)
        {
            return cachedLiveGatewaysSet.Contains(siloAddress);
        }

        internal void ExpediteUpdateLiveGatewaysSnapshot()
        {
            // If there is already an expedited refresh call in place, don't call again, until the previous one is finished.
            // We don't want to issue too many Gateway refresh calls.
            if (gatewayListProvider == null || gatewayRefreshCallInitiated) return;

            // Initiate gateway list refresh asynchronously. The Refresh timer will keep ticking regardless.
            // We don't want to block the client with synchronously Refresh call.
            // Client's call will fail with "No Gateways found" but we will try to refresh the list quickly.
            gatewayRefreshCallInitiated = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await RefreshSnapshotLiveGateways_TimerCallback(null);
                    gatewayRefreshCallInitiated = false;
                }
                catch
                {
                    // Intentionally ignore any exceptions here.
                }
            });
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        internal async Task RefreshSnapshotLiveGateways_TimerCallback(object context)
        {
            try
            {
                if (gatewayListProvider is null) return;

                // the listProvider.GetGateways() is not under lock.
                var allGateways = await gatewayListProvider.GetGateways();
                var refreshedGateways = allGateways.Select(gw => gw.ToGatewayAddress()).ToList();
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("Discovered {GatewayCount} gateways: {Gateways}", refreshedGateways.Count, Utils.EnumerableToString(refreshedGateways));
                }

                await UpdateLiveGatewaysSnapshot(refreshedGateways, gatewayListProvider.MaxStaleness);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.ProxyClient_GetGateways, "Exception occurred during RefreshSnapshotLiveGateways_TimerCallback -> listProvider.GetGateways()", exc);
            }
        }

        // This function is called asynchronously from gateway refresh timer.
        private async Task UpdateLiveGatewaysSnapshot(IEnumerable<SiloAddress> refreshedGateways, TimeSpan maxStaleness)
        {
            List<SiloAddress> connectionsToKeepAlive;

            // This is a short lock, protecting the access to knownDead, knownMasked and cachedLiveGateways.
            lock (lockable)
            {
                // now take whatever listProvider gave us and exclude those we think are dead.
                var live = new List<SiloAddress>();
                var now = DateTime.UtcNow;

                this.knownGateways = refreshedGateways as List<SiloAddress> ?? refreshedGateways.ToList();
                foreach (SiloAddress trial in knownGateways)
                {
                    var address = trial.Generation == 0 ? trial : SiloAddress.New(trial.Endpoint, 0);

                    // We consider a node to be dead if we recorded it is dead due to socket error
                    // and it was recorded (diedAt) not too long ago (less than maxStaleness ago).
                    // The latter is to cover the case when the Gateway provider returns an outdated list that does not yet reflect the actually recently died Gateway.
                    // If it has passed more than maxStaleness - we assume maxStaleness is the upper bound on Gateway provider freshness.
                    var isDead = false;
                    if (knownDead.TryGetValue(address, out var diedAt))
                    {
                        if (now.Subtract(diedAt) < maxStaleness)
                        {
                            isDead = true;
                        }
                        else
                        {
                            // Remove stale entries.
                            knownDead.Remove(address);
                        }
                    }
                    if (knownMasked.TryGetValue(address, out var maskedAt))
                    {
                        if (now.Subtract(maskedAt) < maxStaleness)
                        {
                            isDead = true;
                        }
                        else
                        {
                            // Remove stale entries.
                            knownMasked.Remove(address);
                        }
                    }

                    if (!isDead)
                    {
                        live.Add(address);
                    }
                }

                if (live.Count == 0)
                {
                    logger.Warn(
                        ErrorCode.GatewayManager_AllGatewaysDead,
                        "All gateways have previously been marked as dead. Clearing the list of dead gateways to expedite reconnection.");
                    live.AddRange(knownGateways);
                    knownDead.Clear();
                }

                // swap cachedLiveGateways pointer in one atomic operation
                cachedLiveGateways = live;
                cachedLiveGatewaysSet = new HashSet<SiloAddress>(live);

                DateTime prevRefresh = lastRefreshTime;
                lastRefreshTime = now;
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.Info(ErrorCode.GatewayManager_FoundKnownGateways,
                            "Refreshed the live gateway list. Found {0} gateways from gateway list provider: {1}. Picked only known live out of them. Now has {2} live gateways: {3}. Previous refresh time was = {4}",
                            knownGateways.Count,
                            Utils.EnumerableToString(knownGateways),
                            cachedLiveGateways.Count,
                            Utils.EnumerableToString(cachedLiveGateways),
                            prevRefresh);
                }

                // Close connections to known dead connections, but keep the "masked" ones.
                // Client will not send any new request to the "masked" connections, but might still
                // receive responses
                connectionsToKeepAlive = new List<SiloAddress>(live);
                connectionsToKeepAlive.AddRange(knownMasked.Select(e => e.Key));
            }

            await this.CloseEvictedGatewayConnections(connectionsToKeepAlive);
        }

        private async Task CloseEvictedGatewayConnections(List<SiloAddress> liveGateways)
        {
            if (this.connectionManager == null) return;

            var connectedGateways = this.connectionManager.GetConnectedAddresses();
            foreach (var address in connectedGateways)
            {
                var isLiveGateway = false;
                foreach (var live in liveGateways)
                {
                    if (live.Matches(address))
                    {
                        isLiveGateway = true;
                        break;
                    }
                }

                if (!isLiveGateway)
                {
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        this.logger.LogInformation("Closing connection to {Endpoint} because it has been marked as dead", address);
                    }

                    await this.connectionManager.CloseAsync(address);
                }
            }
        }

        public void Dispose()
        {
            this.gatewayRefreshTimer?.Dispose();
        }
    }
}
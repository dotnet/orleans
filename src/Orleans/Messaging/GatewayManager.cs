using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Messaging
{
    /// <summary>
    /// The GatewayManager class holds the list of known gateways, as well as maintaining the list of "dead" gateways.
    ///
    /// The known list can come from one of two places: the full list may appear in the client configuration object, or
    /// the config object may contain an IGatewayListProvider delegate. If both appear, then the delegate takes priority.
    /// </summary>
    internal class GatewayManager : IGatewayListListener, IDisposable
    {
        internal readonly IGatewayListProvider ListProvider;
        private SafeTimer gatewayRefreshTimer;
        private readonly Dictionary<Uri, DateTime> knownDead;
        private IList<Uri> cachedLiveGateways;
        private DateTime lastRefreshTime;
        private int roundRobinCounter;
        private readonly SafeRandom rand;
        private readonly Logger logger;
        private readonly object lockable;

        private readonly ClientConfiguration config;
        private bool gatewayRefreshCallInitiated;

        public GatewayManager(ClientConfiguration cfg, IGatewayListProvider gatewayListProvider)
        {
            config = cfg;
            knownDead = new Dictionary<Uri, DateTime>();
            rand = new SafeRandom();
            logger = LogManager.GetLogger("Messaging.GatewayManager", LoggerType.Runtime);
            lockable = new object();
            gatewayRefreshCallInitiated = false;

            ListProvider = gatewayListProvider;

            var knownGateways = ListProvider.GetGateways().GetResult();

            if (knownGateways.Count == 0)
            {
                string gatewayProviderType = gatewayListProvider.GetType().FullName;
                string err = String.Format("Could not find any gateway in {0}. Orleans client cannot initialize.", gatewayProviderType);
                logger.Error(ErrorCode.GatewayManager_NoGateways, err);
                throw new OrleansException(err);
            }

            logger.Info(ErrorCode.GatewayManager_FoundKnownGateways, "Found {0} knownGateways from Gateway listProvider {1}", knownGateways.Count, Utils.EnumerableToString(knownGateways));

            if (ListProvider is IGatewayListObservable)
            {
                ((IGatewayListObservable)ListProvider).SubscribeToGatewayNotificationEvents(this);
            }

            roundRobinCounter = cfg.PreferedGatewayIndex >= 0 ? cfg.PreferedGatewayIndex : rand.Next(knownGateways.Count);

            cachedLiveGateways = knownGateways;

            lastRefreshTime = DateTime.UtcNow;
            if (ListProvider.IsUpdatable)
            {
                gatewayRefreshTimer = new SafeTimer(RefreshSnapshotLiveGateways_TimerCallback, null, config.GatewayListRefreshPeriod, config.GatewayListRefreshPeriod);
            }
        }

        public void Stop()
        {
            if (gatewayRefreshTimer != null)
            {
                Utils.SafeExecute(gatewayRefreshTimer.Dispose, logger);
            }
            gatewayRefreshTimer = null;

            if (ListProvider != null && ListProvider is IGatewayListObservable)
            {
                Utils.SafeExecute(
                    () => ((IGatewayListObservable)ListProvider).UnSubscribeFromGatewayNotificationEvents(this),
                    logger);
            }
        }

        public void MarkAsDead(Uri gateway)
        {
            lock (lockable)
            {
                knownDead[gateway] = DateTime.UtcNow;
                var copy = cachedLiveGateways.ToList();
                copy.Remove(gateway);
                // swap the reference, don't mutate cachedLiveGateways, so we can access cachedLiveGateways without the lock.
                cachedLiveGateways = copy;
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
        public Uri GetLiveGateway()
        {
            IList<Uri> live = GetLiveGateways();
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

        public IList<Uri> GetLiveGateways()
        {
            // Never takes a lock and returns the cachedLiveGateways list quickly without any operation.
            // Asynchronously starts gateway refresh only when it is empty.
            if (cachedLiveGateways.Count == 0)
            {
                ExpediteUpdateLiveGatewaysSnapshot();
            }
            return cachedLiveGateways;
        }

        internal void ExpediteUpdateLiveGatewaysSnapshot()
        {
            // If there is already an expedited refresh call in place, don't call again, until the previous one is finished.
            // We don't want to issue too many Gateway refresh calls.
            if (ListProvider == null || !ListProvider.IsUpdatable || gatewayRefreshCallInitiated) return;

            // Initiate gateway list refresh asynchronously. The Refresh timer will keep ticking regardless.
            // We don't want to block the client with synchronously Refresh call.
            // Client's call will fail with "No Gateways found" but we will try to refresh the list quickly.
            gatewayRefreshCallInitiated = true;
            var task = Task.Factory.StartNew(() =>
            {
                RefreshSnapshotLiveGateways_TimerCallback(null);
                gatewayRefreshCallInitiated = false;
            });
            task.Ignore();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public void GatewayListNotification(IEnumerable<Uri> gateways)
        {
            try
            {
                UpdateLiveGatewaysSnapshot(gateways, ListProvider.MaxStaleness);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.ProxyClient_GetGateways, "Exception occurred during GatewayListNotification -> UpdateLiveGatewaysSnapshot", exc);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        internal void RefreshSnapshotLiveGateways_TimerCallback(object context)
        {
            try
            {
                if (ListProvider == null || !ListProvider.IsUpdatable) return;

                // the listProvider.GetGateways() is not under lock.
                var currentKnownGateways = ListProvider.GetGateways().GetResult();
                if (logger.IsVerbose)
                {
                    logger.Verbose("Found {0} knownGateways from Gateway listProvider {1}", currentKnownGateways.Count, Utils.EnumerableToString(currentKnownGateways));
                }

                // the next one will grab the lock.
                UpdateLiveGatewaysSnapshot(currentKnownGateways, ListProvider.MaxStaleness);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.ProxyClient_GetGateways, "Exception occurred during RefreshSnapshotLiveGateways_TimerCallback -> listProvider.GetGateways()", exc);
            }
        }

        // This function is called asynchronously from gateway refresh timer.
        private void UpdateLiveGatewaysSnapshot(IEnumerable<Uri> currentKnownGateways, TimeSpan maxStaleness)
        {
            // this is a short lock, protecting the access to knownDead and cachedLiveGateways.
            lock (lockable)
            {
                // now take whatever listProvider gave us and exclude those we think are dead.

                var live = new List<Uri>();

                var knownGateways = currentKnownGateways as IList<Uri> ?? currentKnownGateways.ToList();
                foreach (Uri trial in knownGateways)
                {
                    DateTime diedAt;
                    // We consider a node to be dead if we recorded it is dead due to socket error
                    // and it was recorded (diedAt) not too long ago (less than maxStaleness ago).
                    // The latter is to cover the case when the Gateway provider returns an outdated list that does not yet reflect the actually recently died Gateway.
                    // If it has passed more than maxStaleness - we assume maxStaleness is the upper bound on Gateway provider freshness.
                    bool isDead = knownDead.TryGetValue(trial, out diedAt) && DateTime.UtcNow.Subtract(diedAt) < maxStaleness;
                    if (!isDead)
                    {
                        live.Add(trial);
                    }
                }

                // swap cachedLiveGateways pointer in one atomic operation
                cachedLiveGateways = live;

                DateTime prevRefresh = lastRefreshTime;
                lastRefreshTime = DateTime.UtcNow;
                if (logger.IsInfo)
                {
                    logger.Info(ErrorCode.GatewayManager_FoundKnownGateways,
                            "Refreshed the live GateWay list. Found {0} gateways from Gateway listProvider: {1}. Picked only known live out of them. Now has {2} live Gateways: {3}. Previous refresh time was = {4}",
                                knownGateways.Count(),
                            Utils.EnumerableToString(knownGateways),
                            cachedLiveGateways.Count,
                            Utils.EnumerableToString(cachedLiveGateways),
                            prevRefresh);
                }
            }
        }

        public void Dispose()
        {
            gatewayRefreshTimer?.Dispose();
        }
    }
}
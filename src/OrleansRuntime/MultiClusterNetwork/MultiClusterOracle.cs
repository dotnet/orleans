using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.MultiCluster;

namespace Orleans.Runtime.MultiClusterNetwork
{
    internal class MultiClusterOracle : SystemTarget, IMultiClusterOracle, ISiloStatusListener, IMultiClusterGossipService
    {
        private readonly MultiClusterGossipChannelFactory channelFactory;
        // as a backup measure, current local active status is sent occasionally
        public static readonly TimeSpan ResendActiveStatusAfter = TimeSpan.FromMinutes(10);

        // time after which this gateway removes other gateways in this same cluster that are known to be gone 
        public static readonly TimeSpan CleanupSilentGoneGatewaysAfter = TimeSpan.FromSeconds(30);

        private readonly MultiClusterOracleData localData;
        private readonly Logger logger;
        private readonly SafeRandom random;
        private readonly string clusterId;
        private readonly IReadOnlyList<string> defaultMultiCluster;

        private readonly TimeSpan backgroundGossipInterval;
        private TimeSpan resendActiveStatusAfter;

        private List<IGossipChannel> gossipChannels;
        private IGrainTimer timer;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly IInternalGrainFactory grainFactory;
        private MultiClusterConfiguration injectedConfig;
        private readonly ILoggerFactory loggerFactory;
        public MultiClusterOracle(SiloInitializationParameters siloDetails, MultiClusterGossipChannelFactory channelFactory, ISiloStatusOracle siloStatusOracle, IInternalGrainFactory grainFactory, ILoggerFactory loggerFactory)
            : base(Constants.MultiClusterOracleId, siloDetails.SiloAddress, loggerFactory)
        {
            this.loggerFactory = loggerFactory;
            this.channelFactory = channelFactory;
            this.siloStatusOracle = siloStatusOracle;
            this.grainFactory = grainFactory;
            if (siloDetails == null) throw new ArgumentNullException(nameof(siloDetails));

            var config = siloDetails.ClusterConfig.Globals;
            logger = new LoggerWrapper<MultiClusterOracle>(loggerFactory);
            localData = new MultiClusterOracleData(logger, grainFactory);
            clusterId = config.ClusterId;
            defaultMultiCluster = config.DefaultMultiCluster;
            random = new SafeRandom();

            // to avoid convoying, each silo varies these period intervals a little
            backgroundGossipInterval = RandomizeTimespanSlightly(config.BackgroundGossipInterval);
            resendActiveStatusAfter = RandomizeTimespanSlightly(ResendActiveStatusAfter);
        }

        // randomize a timespan a little (add between 0% and 5%)
        private TimeSpan RandomizeTimespanSlightly(TimeSpan value)
        {
            return TimeSpan.FromMilliseconds(value.TotalMilliseconds * (1 + (random.NextDouble() * 0.05)));
        }

        public bool IsFunctionalClusterGateway(SiloAddress siloAddress)
        {
            GatewayEntry g;
            return localData.Current.Gateways.TryGetValue(siloAddress, out g)
                && g.Status == GatewayStatus.Active;
        }

        public IEnumerable<string> GetActiveClusters()
        {
            return localData.ActiveGatewaysByCluster.Keys;
        }

        public IEnumerable<GatewayEntry> GetGateways()
        {
            return localData.Current.Gateways.Values;
        }

        public SiloAddress GetRandomClusterGateway(string cluster)
        {
            List<SiloAddress> gatewaylist;

            if (!localData.ActiveGatewaysByCluster.TryGetValue(cluster, out gatewaylist))
                return null;
          
            return gatewaylist[random.Next(gatewaylist.Count)];
        }

        public MultiClusterConfiguration GetMultiClusterConfiguration()
        {
            return localData.Current.Configuration;
        }

        public async Task InjectMultiClusterConfiguration(MultiClusterConfiguration config)
        {
            this.injectedConfig = config;

            logger.Info("Starting MultiClusterConfiguration Injection, configuration={0} ", config);

            PublishChanges();

            // wait for the gossip channel tasks and aggregate exceptions
            var currentChannelTasks = this.channelWorkers.Values.ToList();
            await Task.WhenAll(currentChannelTasks.Select(ct => ct.WaitForCurrentWorkToBeServiced()));

            var exceptions = currentChannelTasks
                .Where(ct => ct.LastException != null)
                .Select(ct => ct.LastException)
                .ToList();

            logger.Info("Completed MultiClusterConfiguration Injection, {0} exceptions", exceptions.Count);

            if (exceptions.Count > 0)
                throw new AggregateException(exceptions);
        }

        public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            // any status change can cause changes in gateway list
            this.ScheduleTask(() => Utils.SafeExecute(() => this.PublishChanges())).Ignore();
        }

        public bool SubscribeToMultiClusterConfigurationEvents(GrainReference observer)
        {
            return localData.SubscribeToMultiClusterConfigurationEvents(observer);
        }

        public bool UnSubscribeFromMultiClusterConfigurationEvents(GrainReference observer)
        {
            return localData.UnSubscribeFromMultiClusterConfigurationEvents(observer);
        }


        /// <inheritdoc/>
        public Func<ILogConsistencyProtocolMessage, bool> ProtocolMessageFilterForTesting { get; set; }


        public async Task Start()
        {
            logger.Info(ErrorCode.MultiClusterNetwork_Starting, "MultiClusterOracle starting on {0}, Severity={1} ", Silo, logger.SeverityLevel);
            try
            {
                if (string.IsNullOrEmpty(clusterId))
                    throw new OrleansException("Internal Error: missing cluster id");
                
                gossipChannels = await this.channelFactory.CreateGossipChannels();

                if (gossipChannels.Count == 0)
                    logger.Warn(ErrorCode.MultiClusterNetwork_NoChannelsConfigured, "No gossip channels are configured.");
                
                // startup: pull all the info from the tables, then inject default multi cluster if none found
                foreach (var ch in gossipChannels)
                {
                    GetChannelWorker(ch).Synchronize();
                }

                await Task.WhenAll(this.channelWorkers.Select(kvp => kvp.Value.WaitForCurrentWorkToBeServiced()));
                if (GetMultiClusterConfiguration() == null && defaultMultiCluster != null)
                {
                    this.injectedConfig = new MultiClusterConfiguration(DateTime.UtcNow, defaultMultiCluster, "DefaultMultiCluster");
                    logger.Info("No configuration found. Using default configuration {0} ", this.injectedConfig);
                }

                this.siloStatusOracle.SubscribeToSiloStatusEvents(this);

                PublishChanges();

                StartTimer(); // for periodic full bulk gossip

                logger.Info(ErrorCode.MultiClusterNetwork_Starting, "MultiClusterOracle started on {0} ", Silo);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.MultiClusterNetwork_FailedToStart, "MultiClusterOracle failed to start {0}", exc);
                throw;
            }
        }

        private void StartTimer()
        {
            if (timer != null)
                timer.Dispose();

            timer = GrainTimer.FromTimerCallback(
                this.RuntimeClient.Scheduler,
                this.loggerFactory.CreateLogger<GrainTimer>(),
                this.OnGossipTimerTick,
                null,
                this.backgroundGossipInterval,
                this.backgroundGossipInterval,
                "MultiCluster.GossipTimer");

            timer.Start();
        }

        private void OnGossipTimerTick(object _)
        {
            logger.Verbose3("-timer");
            PublishChanges();
            PeriodicBackgroundGossip();
        }

        // called in response to changed status, and periodically
        private void PublishChanges()
        {
            logger.Verbose("--- PublishChanges: assess");

            var activeLocalGateways = this.siloStatusOracle.GetApproximateMultiClusterGateways();

            var iAmGateway = activeLocalGateways.Contains(Silo);

            // collect deltas that need to be published to all other gateways. 
            // Most of the time, this will contain just zero or one change.
            var deltas = new MultiClusterData();

            // Determine local status, and add to deltas if it changed
            InjectLocalStatus(iAmGateway, ref deltas);

            // Determine if admin has injected a new configuration, and add to deltas if that is the case
            InjectConfiguration(ref deltas);

            // Determine if there are some stale gateway entries of this cluster that should be demoted, 
            // and add those demotions to deltas
            if (iAmGateway)
                DemoteLocalGateways(activeLocalGateways, ref deltas);

            if (logger.IsVerbose)
                logger.Verbose("--- PublishChanges: found activeGateways={0} iAmGateway={1} publish={2}",
                   string.Join(",", activeLocalGateways), iAmGateway, deltas);

            if (!deltas.IsEmpty)
            {
                // Now we do the actual publishing. Note that we publish deltas only once and 
                // simply log any errors without retrying. To handle problems 
                // caused by lost messages we rely instead on the periodic background gossip: 
                // each node periodically does full two-way gossip (Synchronize) with 
                // some random other node or channel. This ensures all information 
                // eventually gets everywhere.

                // publish deltas to all remote clusters 
                foreach (var x in this.AllClusters().Where(x => x != this.clusterId))
                {
                    GetClusterWorker(x).Publish(deltas);
                }

                // publish deltas to all local silos
                var activeLocalClusterSilos = this.GetApproximateOtherActiveSilos();

                foreach (var activeLocalClusterSilo in activeLocalClusterSilos)
                {
                    GetSiloWorker(activeLocalClusterSilo).Publish(deltas);
                }

                // publish deltas to all gossip channels
                foreach (var ch in gossipChannels)
                {
                    GetChannelWorker(ch).Publish(deltas);
                }
            }

            if (deltas.Gateways.ContainsKey(this.Silo) && deltas.Gateways[this.Silo].Status == GatewayStatus.Active)
            {
                // Fully synchronize with channels if we just went active, which helps with initial startup time.
                // Note: doing a partial publish just before this full synchronize is by design, so that it reduces stabilization
                // time when several Silos are starting up at the same time, and there already is information about each other
                // before they attempt the full gossip
                foreach (var ch in gossipChannels)
                {
                    GetChannelWorker(ch).Synchronize();
                }
            }

            logger.Verbose("--- PublishChanges: done");
        }

        private IEnumerable<SiloAddress> GetApproximateOtherActiveSilos()
        {
            return this.siloStatusOracle.GetApproximateSiloStatuses()
                .Where(kvp => !kvp.Key.Equals(this.Silo) && kvp.Value == SiloStatus.Active)
                .Select(kvp => kvp.Key);
        }

        private void PeriodicBackgroundGossip()
        {
            logger.Verbose("--- PeriodicBackgroundGossip");
            // pick random target for full gossip
            var gateways = localData.Current.Gateways.Values
                           .Where(gw => !gw.SiloAddress.Equals(this.Silo) && gw.Status == GatewayStatus.Active)
                           .ToList();
            var pick = random.Next(gateways.Count + gossipChannels.Count);
            if (pick < gateways.Count)
            {
                var cluster = gateways[pick].ClusterId;
                GetClusterWorker(cluster).Synchronize();
            }
            else
            {
                var address = gossipChannels[pick - gateways.Count];
                GetChannelWorker(address).Synchronize();
            }

            // report summary of encountered communication problems in log
            var unreachableClusters = string.Join(",", this.clusterWorkers
                .Where(kvp => kvp.Value.LastException != null)
                .Select(kvp => string.Format("{0}({1})", kvp.Key, kvp.Value.LastException.GetType().Name)));
            if (!string.IsNullOrEmpty(unreachableClusters))
                logger.Warn(ErrorCode.MultiClusterNetwork_GossipCommunicationFailure, "Gossip Communication: cannot reach clusters {0}", unreachableClusters);

            var unreachableSilos = string.Join(",", this.siloWorkers
                .Where(kvp => kvp.Value.LastException != null)
                .Select(kvp => string.Format("{0}({1})", kvp.Key, kvp.Value.LastException.GetType().Name)));
            if (!string.IsNullOrEmpty(unreachableSilos))
                logger.Warn(ErrorCode.MultiClusterNetwork_GossipCommunicationFailure, "Gossip Communication: cannot reach silos {0}", unreachableSilos);

            var unreachableChannels = string.Join(",", this.channelWorkers
                  .Where(kvp => kvp.Value.LastException != null)
                  .Select(kvp => string.Format("{0}({1})", kvp.Key, kvp.Value.LastException.GetType().Name)));
            if (!string.IsNullOrEmpty(unreachableChannels))
                logger.Warn(ErrorCode.MultiClusterNetwork_GossipCommunicationFailure, "Gossip Communication: cannot reach channels {0}", unreachableChannels);

            // discard workers that have not been used for a while and are idle
            RemoveIdleWorkers(this.clusterWorkers);
            RemoveIdleWorkers(this.siloWorkers);

            logger.Verbose("--- PeriodicBackgroundGossip: done");
        }

        // the set of all known clusters
        private IEnumerable<string> AllClusters()
        {
            var allClusters = localData.Current.Gateways.Values.Select(gw => gw.ClusterId);
            if (localData.Current.Configuration != null)
            {
                allClusters = allClusters.Union(localData.Current.Configuration.Clusters);
            }

            return new HashSet<string>(allClusters);
        }

        private void RemoveIdleWorkers<K, T>(Dictionary<K, T> dict) where T : GossipWorker
        {
            var now = DateTime.UtcNow;
            var toRemove = dict
                .Where(kvp => (now - kvp.Value.LastUse).TotalMilliseconds > 2.5 * this.resendActiveStatusAfter.TotalMilliseconds)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in toRemove)
                dict.Remove(key);
        }

        // called by remote nodes that publish changes
        public Task Publish(IMultiClusterGossipData gossipData, bool forwardLocally)
        {
            logger.Verbose("--- Publish: receive {0} data {1}", forwardLocally ? "remote" : "local", gossipData);

            var data = (MultiClusterData)gossipData;

            var delta = localData.ApplyDataAndNotify(data);

            // forward changes to all local silos
            if (forwardLocally)
            {
                foreach (var activeSilo in this.GetApproximateOtherActiveSilos())
                    GetSiloWorker(activeSilo).Publish(delta);
            }

            PublishMyStatusToNewDestinations(delta);

            logger.Verbose("--- Publish: done");

            return Task.CompletedTask;
        }

        // called by remote nodes' full background gossip
        public Task<IMultiClusterGossipData> Synchronize(IMultiClusterGossipData gossipData)
        {
            logger.Verbose("--- Synchronize: gossip {0}", gossipData);

            var data = (MultiClusterData)gossipData;

            var delta = this.localData.ApplyDataAndNotify(data);

            PublishMyStatusToNewDestinations(delta);

            logger.Verbose("--- Synchronize: done, answer={0}", delta);

            return Task.FromResult((IMultiClusterGossipData)delta);
        }

        // initiate a search for lagging silos, contacting other silos and clusters
        public async Task<List<SiloAddress>> FindLaggingSilos(MultiClusterConfiguration expected)
        {
            var tasks = new List<Task<List<SiloAddress>>>();

            // check this cluster for lagging silos
            tasks.Add(FindLaggingSilos(expected, true));

            // check all other clusters for lagging silos
            foreach (var cluster in GetActiveClusters())
            {
                if (cluster != this.clusterId)
                {
                    var silo = GetRandomClusterGateway(cluster);
                    if (silo == null)
                        throw new OrleansException("no gateway for cluster " + cluster);
                    var remoteOracle = this.grainFactory.GetSystemTarget<IMultiClusterGossipService>(Constants.MultiClusterOracleId, silo);
                    tasks.Add(remoteOracle.FindLaggingSilos(expected, true));
                }
            }

            // This function is called  during manual admin operations through 
            // IManagementGrain (change configuration, or check stability).
            // Users are going to want to see the exception details to figure out 
            // what is going on.
            await Task.WhenAll(tasks);

            return tasks.SelectMany(t => t.Result).ToList();
        }

        // receive a remote request for finding lagging silos in this cluster or on this silo
        public async Task<List<SiloAddress>> FindLaggingSilos(MultiClusterConfiguration expected, bool forwardLocally)
        {
            logger.Verbose("--- FindLaggingSilos: {0}, {1}", forwardLocally ? "remote" : "local", expected);

            var result = new List<SiloAddress>();

            // check if this silo is lagging
            if (!MultiClusterConfiguration.Equals(localData.Current.Configuration, expected))
                result.Add(this.Silo);

            if (forwardLocally)
            {
                // contact all other active silos in this cluster

                var tasks = new List<Task<List<SiloAddress>>>();

                foreach (var activeSilo in this.GetApproximateOtherActiveSilos())
                {
                    var remoteOracle = this.grainFactory.GetSystemTarget<IMultiClusterGossipService>(Constants.MultiClusterOracleId, activeSilo);
                    tasks.Add(remoteOracle.FindLaggingSilos(expected, false));
                }

                await Task.WhenAll(tasks);

                foreach (var silo in tasks.SelectMany(t => t.Result))
                {
                    result.Add(silo);
                }
            }

            logger.Verbose("--- FindLaggingSilos: done, found {0}", result.Count);

            return result;
        }

        private void PublishMyStatusToNewDestinations(MultiClusterData delta)
        {
            // for quicker convergence, we publish active local status information
            // immediately when we learn about a new destination

            GatewayEntry myEntry;

            // don't do this if we are not an active gateway
            if (!localData.Current.Gateways.TryGetValue(this.Silo, out myEntry)
                || myEntry.Status != GatewayStatus.Active)
                return;

            foreach (var gateway in delta.Gateways.Values)
            {
                var gossipworker = (gateway.ClusterId == this.clusterId) ?
                    GetSiloWorker(gateway.SiloAddress) : GetClusterWorker(gateway.ClusterId);

                var destinationCluster = gateway.ClusterId;

                if (!gossipworker.KnowsMe)
                    gossipworker.Publish(new MultiClusterData(myEntry));
            }
        }


        // gossip workers, by category
        private readonly Dictionary<SiloAddress, SiloGossipWorker> siloWorkers = new Dictionary<SiloAddress, SiloGossipWorker>();
        private readonly Dictionary<string, SiloGossipWorker> clusterWorkers = new Dictionary<string, SiloGossipWorker>();
        private readonly Dictionary<IGossipChannel, ChannelGossipWorker> channelWorkers = new Dictionary<IGossipChannel, ChannelGossipWorker>();

        // numbering for tasks (helps when analyzing logs)
        private int idCounter;

        private SiloGossipWorker GetSiloWorker(SiloAddress silo)
        {
            if (silo == null) throw new ArgumentNullException("silo");
            SiloGossipWorker worker;
            if (!this.siloWorkers.TryGetValue(silo, out worker))
                this.siloWorkers[silo] = worker = new SiloGossipWorker(this, silo, this.grainFactory);
            return worker;
        }
        private SiloGossipWorker GetClusterWorker(string cluster)
        {
            if (cluster == null) throw new ArgumentNullException("cluster");
            SiloGossipWorker worker;
            if (!this.clusterWorkers.TryGetValue(cluster, out worker))
                this.clusterWorkers[cluster] = worker = new SiloGossipWorker(this, cluster, this.grainFactory);
            return worker;
        }
        private ChannelGossipWorker GetChannelWorker(IGossipChannel channel)
        {
            if (channel == null) throw new ArgumentNullException("channel");
            ChannelGossipWorker worker;
            if (!this.channelWorkers.TryGetValue(channel, out worker))
                this.channelWorkers[channel] = worker = new ChannelGossipWorker(this, channel);
            return worker;
        }

        // superclass for gossip workers.
        // a gossip worker queues (push) and (synchronize) requests, 
        // and services them using a single async worker
        private abstract class GossipWorker : BatchWorker
        {
            public GossipWorker(MultiClusterOracle oracle)
            {
                this.oracle = oracle;
            }
            protected MultiClusterOracle oracle;

            // add all data to be published into this variable
            protected MultiClusterData toPublish = new MultiClusterData();

            // set this flag to request a full gossip (synchronize)
            protected bool doSynchronize = false;

            public void Publish(MultiClusterData data)
            {
                // add the data to the data waiting to be published
                toPublish = toPublish.Merge(data);

                if (oracle.logger.IsVerbose)
                   LogQueuedPublish(toPublish);

                Notify();
            }
            public void Synchronize()
            {
                doSynchronize = true;
                Notify();
            }

            public Exception LastException;
            public DateTime LastUse = DateTime.UtcNow;

            protected override async Task Work()
            {
                // publish data that has been queued
                var data = toPublish;
                if (!data.IsEmpty)
                {
                    toPublish = new MultiClusterData(); // clear queued data
                    int id = ++oracle.idCounter;
                    LastUse = DateTime.UtcNow;
                    await Publish(id, data);
                    LastUse = DateTime.UtcNow;
                };

                // do a full synchronize if flag is set
                if (doSynchronize)
                {
                    doSynchronize = false; // clear flag

                    int id = ++oracle.idCounter;
                    LastUse = DateTime.UtcNow;
                    await Synchronize(id);
                    LastUse = DateTime.UtcNow;
                }
            }

            protected abstract Task Publish(int id, MultiClusterData data);
            protected abstract Task Synchronize(int id);
            protected abstract void LogQueuedPublish(MultiClusterData data);
        }

        // A worker for gossiping with silos
        private class SiloGossipWorker : GossipWorker
        {
            public SiloAddress Silo;
            private readonly IInternalGrainFactory grainFactory;
            public string Cluster;

            public bool TargetsRemoteCluster { get { return Cluster != null; } }

            public bool KnowsMe; // used for optimizing pushes

            public SiloGossipWorker(MultiClusterOracle oracle, SiloAddress Silo, IInternalGrainFactory grainFactory)
                : base(oracle)
            {
                this.Cluster = null; // only local cluster
                this.Silo = Silo;
                this.grainFactory = grainFactory;
            }

            public SiloGossipWorker(MultiClusterOracle oracle, string cluster, IInternalGrainFactory grainFactory)
               : base(oracle)
            {

                this.Cluster = cluster;
                this.Silo = null;
                this.grainFactory = grainFactory;
            }

            protected override void LogQueuedPublish(MultiClusterData data)
            {
                if (TargetsRemoteCluster)
                    oracle.logger.Verbose("enqueued publish to cluster {0}, cumulative: {1}", Cluster, data);
                else
                    oracle.logger.Verbose("enqueued publish to silo {0}, cumulative: {1}", Silo, data);
            }

            protected async override Task Publish(int id, MultiClusterData data)
            {
                // optimization: can skip publish to local clusters if we are doing a full synchronize anyway
                if (!TargetsRemoteCluster && doSynchronize)
                    return;

                // for remote clusters, pick a random gateway if we don't already have one, or it is not active anymore
                if (TargetsRemoteCluster && (Silo == null
                    || !oracle.localData.Current.IsActiveGatewayForCluster(Silo, Cluster)))
                {
                    Silo = oracle.GetRandomClusterGateway(Cluster);
                }

                oracle.logger.Verbose("-{0} Publish to silo {1} ({2}) {3}", id, Silo, Cluster ?? "local", data);
                try
                {
                    // publish to the remote system target
                    var remoteOracle = this.grainFactory.GetSystemTarget<IMultiClusterGossipService>(Constants.MultiClusterOracleId, Silo);
                    await remoteOracle.Publish(data, TargetsRemoteCluster);

                    LastException = null;
                    if (data.Gateways.ContainsKey(oracle.Silo))
                        KnowsMe = data.Gateways[oracle.Silo].Status == GatewayStatus.Active;
                    oracle.logger.Verbose("-{0} Publish to silo successful", id);
                }
                catch (Exception e)
                {
                    oracle.logger.Warn(ErrorCode.MultiClusterNetwork_GossipCommunicationFailure,
                        $"-{id} Publish to silo {Silo} ({Cluster ?? "local"}) failed", e);
                    if (TargetsRemoteCluster)
                        Silo = null; // pick a different gateway next time
                    LastException = e;
                }
            }

            protected async override Task Synchronize(int id)
            {

                // for remote clusters, always pick another random gateway
                if (TargetsRemoteCluster)
                    Silo = oracle.GetRandomClusterGateway(Cluster);

                oracle.logger.Verbose("-{0} Synchronize with silo {1} ({2})", id, Silo, Cluster ?? "local");
                try
                {
                    var remoteOracle = this.grainFactory.GetSystemTarget<IMultiClusterGossipService>(Constants.MultiClusterOracleId, Silo);
                    var data = oracle.localData.Current;
                    var answer = (MultiClusterData)await remoteOracle.Synchronize(oracle.localData.Current);

                    // apply what we have learnt
                    var delta = oracle.localData.ApplyDataAndNotify(answer);

                    LastException = null;
                    if (data.Gateways.ContainsKey(oracle.Silo))
                        KnowsMe = data.Gateways[oracle.Silo].Status == GatewayStatus.Active;
                    oracle.logger.Verbose("-{0} Synchronize with silo successful, answer={1}", id, answer);

                    oracle.PublishMyStatusToNewDestinations(delta);
                }
                catch (Exception e)
                {
                    oracle.logger.Warn(ErrorCode.MultiClusterNetwork_GossipCommunicationFailure,
                        string.Format("-{0} Synchronize with silo {1} ({2}) failed", id, Silo, Cluster ?? "local"), e);
                    if (TargetsRemoteCluster)
                        Silo = null; // pick a different gateway next time
                    LastException = e;
                }
            }
        }


        // A worker for gossiping with channels
        private class ChannelGossipWorker : GossipWorker
        {
            IGossipChannel channel;

            public ChannelGossipWorker(MultiClusterOracle oracle, IGossipChannel channel)
                : base(oracle)
            {
                this.channel = channel;
            }

            protected override void LogQueuedPublish(MultiClusterData data)
            {
                oracle.logger.Verbose("enqueue publish to channel {0}, cumulative: {1}", channel.Name, data);
            }

            protected async override Task Publish(int id, MultiClusterData data)
            {
                oracle.logger.Verbose("-{0} Publish to channel {1} {2}", id, channel.Name, data);
                try
                {
                    await channel.Publish(data);
                    LastException = null;
                    oracle.logger.Verbose("-{0} Publish to channel successful, answer={1}", id, data);
                }
                catch (Exception e)
                {
                    oracle.logger.Warn(ErrorCode.MultiClusterNetwork_GossipCommunicationFailure,
                        string.Format("-{0} Publish to channel {1} failed", id, channel.Name), e);
                    LastException = e;
                }
            }
            protected async override Task Synchronize(int id)
            {
                oracle.logger.Verbose("-{0} Synchronize with channel {1}", id, channel.Name);
                try
                {
                    var answer = await channel.Synchronize(oracle.localData.Current);

                    // apply what we have learnt
                    var delta = oracle.localData.ApplyDataAndNotify(answer);

                    LastException = null;
                    oracle.logger.Verbose("-{0} Synchronize with channel successful", id);

                    oracle.PublishMyStatusToNewDestinations(delta);
                }
                catch (Exception e)
                {
                    oracle.logger.Warn(ErrorCode.MultiClusterNetwork_GossipCommunicationFailure,
                        string.Format("-{0} Synchronize with channel {1} failed", id, channel.Name), e);
                    LastException = e;
                }
            }
        }
    

        private void InjectConfiguration(ref MultiClusterData deltas)
        {
            if (this.injectedConfig == null)
                return;

            var data = new MultiClusterData(this.injectedConfig);
            this.injectedConfig = null;

            if (logger.IsVerbose)
                logger.Verbose("-InjectConfiguration {0}", data.Configuration.ToString());

            var delta = this.localData.ApplyDataAndNotify(data);

            if (!delta.IsEmpty)
                deltas = deltas.Merge(delta);
        }

        private void InjectLocalStatus(bool isGateway, ref MultiClusterData deltas)
        {
            var myStatus = new GatewayEntry()
            {
                ClusterId = clusterId,
                SiloAddress = Silo,
                Status = isGateway ? GatewayStatus.Active : GatewayStatus.Inactive,
                HeartbeatTimestamp = DateTime.UtcNow,
            };

            GatewayEntry existingEntry;

            // do not update if we are reporting inactive status and entry is not already there
            if (!this.localData.Current.Gateways.TryGetValue(Silo, out existingEntry) && !isGateway)
                return;

            // send if status is changed, or we are active and haven't said so in a while
            if (existingEntry == null
                || existingEntry.Status != myStatus.Status
                || (myStatus.Status == GatewayStatus.Active
                      && myStatus.HeartbeatTimestamp - existingEntry.HeartbeatTimestamp > this.resendActiveStatusAfter))
            {
                logger.Verbose2("-InjectLocalStatus {0}", myStatus);

                // update current data with status
                var delta = this.localData.ApplyDataAndNotify(new MultiClusterData(myStatus));

                if (!delta.IsEmpty)
                    deltas = deltas.Merge(delta);
            }
        }

        private void DemoteLocalGateways(IReadOnlyList<SiloAddress> activeGateways, ref MultiClusterData deltas)
        {
            var now = DateTime.UtcNow;

            // mark gateways as inactive if they have not recently advertised their existence,
            // and if they are not designated gateways as per membership table
            var toBeUpdated = this.localData.Current.Gateways.Values
                .Where(g => g.ClusterId == clusterId
                       && g.Status == GatewayStatus.Active
                       && (now - g.HeartbeatTimestamp > CleanupSilentGoneGatewaysAfter)
                       && !activeGateways.Contains(g.SiloAddress))
                .Select(g => new GatewayEntry()
                {
                    ClusterId = g.ClusterId,
                    SiloAddress = g.SiloAddress,
                    Status = GatewayStatus.Inactive,
                    HeartbeatTimestamp = g.HeartbeatTimestamp + CleanupSilentGoneGatewaysAfter,
                }).ToList();

            if (toBeUpdated.Count == 0)
                return;

            var data = new MultiClusterData(toBeUpdated);

            if (logger.IsVerbose)
                logger.Verbose("-DemoteLocalGateways {0}", data.ToString());
 
            var delta = this.localData.ApplyDataAndNotify(data);

            if (!delta.IsEmpty)
            {
                deltas = deltas.Merge(delta);
            }
        }
    }
}

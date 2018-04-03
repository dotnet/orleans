using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.SystemTargetInterfaces;
using Orleans.Runtime.Scheduler;
using OutcomeState = Orleans.Runtime.GrainDirectory.GlobalSingleInstanceResponseOutcome.OutcomeState;
using Orleans.Runtime.MultiClusterNetwork;
using Orleans.MultiCluster;
using System.Threading;

namespace Orleans.Runtime.GrainDirectory
{
    internal class GlobalSingleInstanceActivationMaintainer : DedicatedAsynchAgent, IMultiClusterConfigurationListener
    {
        private readonly object lockable = new object();
        private readonly LocalGrainDirectory router;
        private readonly ILogger logger;
        private readonly IInternalGrainFactory grainFactory;
        private readonly TimeSpan period;
        private readonly IMultiClusterOracle multiClusterOracle;
        private readonly ILocalSiloDetails siloDetails;
        private readonly MultiClusterOptions multiClusterOptions;

        // scanning the entire directory for doubtful activations is too slow.
        // therefore, we maintain a list of potentially doubtful activations on the side.
        // maintainer periodically takes and processes this list.
        private List<GrainId> doubtfulGrains = new List<GrainId>();

        // used to cut short the waiting time before next run
        private ManualResetEvent runNow = new ManualResetEvent(false);

        public GlobalSingleInstanceActivationMaintainer(
            LocalGrainDirectory router,
            ILogger logger,
            IInternalGrainFactory grainFactory,
            IMultiClusterOracle multiClusterOracle,
            ExecutorService executorService,
            ILocalSiloDetails siloDetails,
            IOptions<MultiClusterOptions> multiClusterOptions,
            ILoggerFactory loggerFactory)
            : base(executorService, loggerFactory)
        {
            this.router = router;
            this.logger = logger;
            this.grainFactory = grainFactory;
            this.multiClusterOracle = multiClusterOracle;
            this.siloDetails = siloDetails;
            this.multiClusterOptions = multiClusterOptions.Value;
            this.period = multiClusterOptions.Value.GlobalSingleInstanceRetryInterval;
            multiClusterOracle.SubscribeToMultiClusterConfigurationEvents(this);
            logger.Debug("GSIP:M GlobalSingleInstanceActivationMaintainer Started, Period = {0}", period);
        }

        public void TrackDoubtfulGrain(GrainId grain)
        {
            lock (lockable)
                doubtfulGrains.Add(grain);
        }

        public void TrackDoubtfulGrains(Dictionary<GrainId, IGrainInfo> newstuff)
        {
            var newdoubtful = FilterByMultiClusterStatus(newstuff, GrainDirectoryEntryStatus.Doubtful)
                .Select(kvp => kvp.Key)
                .ToList();

            lock (lockable)
            {
                doubtfulGrains.AddRange(newdoubtful);
            }
        }

        public static IEnumerable<KeyValuePair<GrainId, IGrainInfo>> FilterByMultiClusterStatus(Dictionary<GrainId, IGrainInfo> collection, GrainDirectoryEntryStatus status)
        {
            foreach (var kvp in collection)
            {
                if (!kvp.Value.SingleInstance)
                    continue;
                var act = kvp.Value.Instances.FirstOrDefault();
                if (act.Key == null)
                    continue;
                if (act.Value.RegistrationStatus == status)
                    yield return kvp;
            }
        }

        // the following method runs for the whole lifetime of the silo, doing the periodic maintenance
        protected override void Run()
        {
            if (!this.multiClusterOptions.HasMultiClusterNetwork)
                return;

            var myClusterId = this.siloDetails.ClusterId;

            Cts.Token.Register(this.Prod);

            while (router.Running && !Cts.IsCancellationRequested)
            {
                try
                {
                    // wait until it is time, or someone prodded us to continue
                    runNow.WaitOne(period);
                    runNow.Reset();

                    if (!router.Running || Cts.IsCancellationRequested) break;

                    logger.Debug("GSIP:M running check");

                    // examine the multicluster configuration
                    var multiClusterConfig = this.multiClusterOracle.GetMultiClusterConfiguration();
                    if (multiClusterConfig == null || !multiClusterConfig.Clusters.Contains(myClusterId))
                    {
                        // we are not joined to the cluster yet/anymore. 
                        // go through all owned entries and make them doubtful
                        // this will not happen under normal operation
                        // (because nodes are supposed to shut down before being removed from the multi cluster)
                        // but if it happens anyway, this is the correct thing to do

                        var allEntries = router.DirectoryPartition.GetItems();
                        var ownedEntries = FilterByMultiClusterStatus(allEntries, GrainDirectoryEntryStatus.Owned)
                            .Select(kp => Tuple.Create(kp.Key, kp.Value.Instances.FirstOrDefault()))
                            .ToList();

                        logger.Debug("GSIP:M Not joined to multicluster. Make {0} owned entries doubtful {1}", ownedEntries.Count, logger.IsEnabled(LogLevel.Trace) ? string.Join(",", ownedEntries.Select(s => s.Item1)) : "");

                        router.Scheduler.QueueTask(
                            () => RunBatchedDemotion(ownedEntries),
                            router.CacheValidator.SchedulingContext
                        ).Wait();
                    }
                    else
                    {
                        // we are joined to the multicluster.

                        List<KeyValuePair<string, SiloAddress>> remoteClusters = multiClusterConfig.Clusters
                            .Where(id => id != myClusterId)
                            .Select(id => new KeyValuePair<string, SiloAddress>(id, this.multiClusterOracle.GetRandomClusterGateway(id)))
                            .ToList();

                        if (!remoteClusters.Any(kvp => kvp.Value == null))
                        {
                            // all clusters have at least one gateway reporting.
                            // go through all doubtful entries and broadcast ownership requests for each

                            List<GrainId> grains;
                            lock (lockable)
                            {
                                grains = doubtfulGrains;
                                doubtfulGrains = new List<GrainId>();
                            }

                            logger.Debug("GSIP:M retry {0} doubtful entries {1}", grains.Count, logger.IsEnabled(LogLevel.Trace) ? string.Join(",", grains) : "");

                            router.Scheduler.QueueTask(
                                () => RunBatchedActivationRequests(remoteClusters, grains),
                                router.CacheValidator.SchedulingContext
                            ).Wait();
                        }
                    }
                }
                catch (Exception e)
                {
                    logger.Error(ErrorCode.GlobalSingleInstance_MaintainerException,
                            "GSIP:M caught exception", e);
                }
            }
        }
        private Task RunBatchedDemotion(List<Tuple<GrainId, KeyValuePair<ActivationId, IActivationInfo>>> entries)
        {
            foreach (var entry in entries)
            {
                router.DirectoryPartition.UpdateClusterRegistrationStatus(entry.Item1, entry.Item2.Key, GrainDirectoryEntryStatus.Doubtful, GrainDirectoryEntryStatus.Owned);
                TrackDoubtfulGrain(entry.Item1);
            }

            return Task.CompletedTask;
        }

        private async Task RunBatchedActivationRequests(List<KeyValuePair<string, SiloAddress>> remoteClusters, List<GrainId> grains)
        {
            var addresses = new List<ActivationAddress>();

            foreach (var grain in grains)
            {
                // retrieve activation
                ActivationAddress address;
                int version;
                var mcstate = router.DirectoryPartition.TryGetActivation(grain, out address, out version);

                // work on the doubtful ones only
                if (mcstate == GrainDirectoryEntryStatus.Doubtful)
                {
                    // try to start retry by moving into requested_ownership state
                    if (router.DirectoryPartition.UpdateClusterRegistrationStatus(grain, address.Activation, GrainDirectoryEntryStatus.RequestedOwnership, GrainDirectoryEntryStatus.Doubtful))
                    {
                        addresses.Add(address);
                    }
                }
            }

            if (addresses.Count == 0)
                return;

            var batchResponses = new List<RemoteClusterActivationResponse[]>();
                   
            var tasks = remoteClusters.Select(async remotecluster => 
            {
                // find gateway and send batched request
                try
                {
                    var clusterGrainDir = this.grainFactory.GetSystemTarget<IClusterGrainDirectory>(Constants.ClusterDirectoryServiceId, remotecluster.Value);
                    var r = await clusterGrainDir.ProcessActivationRequestBatch(addresses.Select(a => a.Grain).ToArray(), this.siloDetails.ClusterId).WithCancellation(Cts.Token);
                    batchResponses.Add(r);
                }
                catch (Exception e)
                {
                    batchResponses.Add(
                        Enumerable.Repeat<RemoteClusterActivationResponse>(
                           new RemoteClusterActivationResponse(ActivationResponseStatus.Faulted)
                           {
                               ResponseException = e
                           }, addresses.Count).ToArray());
                }

            }).ToList();
 
            // wait for all the responses to arrive or fail
            await Task.WhenAll(tasks);

            if (logger.IsEnabled(LogLevel.Debug))
            { 
                foreach (var br in batchResponses)
                {
                    var summary = br.Aggregate(new { Pass = 0, Failed = 0, FailedA = 0, FailedOwned = 0, Faulted = 0 }, (agg, r) =>
                    {
                        switch (r.ResponseStatus)
                        {
                            case ActivationResponseStatus.Pass:
                                return new { Pass = agg.Pass + 1, agg.Failed, agg.FailedA, agg.FailedOwned, agg.Faulted };
                            case ActivationResponseStatus.Failed:
                                if (!r.Owned)
                                {
                                    return r.ExistingActivationAddress.Address == null
                                        ? new { agg.Pass, Failed = agg.Failed + 1, agg.FailedA, agg.FailedOwned, agg.Faulted }
                                        : new { agg.Pass, agg.Failed, FailedA = agg.FailedA + 1, agg.FailedOwned, agg.Faulted };
                                }
                                else
                                {
                                    return new { agg.Pass, agg.Failed, agg.FailedA, FailedOwned = agg.FailedOwned + 1, agg.Faulted };
                                }
                            default:
                                return new { agg.Pass, agg.Failed, agg.FailedA, agg.FailedOwned, Faulted = agg.Faulted + 1 };
                        }
                    });
                    logger.Debug("GSIP:M batchresponse PASS:{0} FAILED:{1} FAILED(a){2}: FAILED(o){3}: FAULTED:{4}",
                        summary.Pass,
                        summary.Failed,
                        summary.FailedA,
                        summary.FailedOwned,
                        summary.Faulted);
                }
            }

            // process each address

            var loser_activations_per_silo = new Dictionary<SiloAddress, List<ActivationAddress>>();

            for (int i = 0; i < addresses.Count; i++)
            {
                var address = addresses[i];

                // array that holds the responses
                var responses = new RemoteClusterActivationResponse[remoteClusters.Count];

                for (int j = 0; j < batchResponses.Count; j++)
                    responses[j] = batchResponses[j][i];

                // response processor
                var outcomeDetails = GlobalSingleInstanceResponseTracker.GetOutcome(responses, address.Grain, logger);
                var outcome = outcomeDetails.State;

                if (logger.IsEnabled(LogLevel.Trace))
                    logger.Trace("GSIP:M {0} Result={1}", address.Grain, outcomeDetails);

                switch (outcome)
                {
                    case OutcomeState.RemoteOwner:
                    case OutcomeState.RemoteOwnerLikely:
                    {
                        // record activations that lost and need to be deactivated
                        List<ActivationAddress> losers;
                        if (!loser_activations_per_silo.TryGetValue(address.Silo, out losers))
                            loser_activations_per_silo[address.Silo] = losers = new List<ActivationAddress>();
                        losers.Add(address);

                        router.DirectoryPartition.CacheOrUpdateRemoteClusterRegistration(address.Grain, address.Activation, outcomeDetails.RemoteOwnerAddress.Address);
                        continue;
                    }
                    case OutcomeState.Succeed:
                    {
                        var ok = (router.DirectoryPartition.UpdateClusterRegistrationStatus(address.Grain, address.Activation, GrainDirectoryEntryStatus.Owned, GrainDirectoryEntryStatus.RequestedOwnership));
                        if (ok)
                            continue;
                        else
                            break;
                    }
                    case OutcomeState.Inconclusive:
                    {
                        break;
                    }
                }

                // we were not successful, reread state to determine what is going on
                int version;
                var mcstatus = router.DirectoryPartition.TryGetActivation(address.Grain, out address, out version);

                // in each case, go back to DOUBTFUL
                if (mcstatus == GrainDirectoryEntryStatus.RequestedOwnership)
                {
                    // we failed because of inconclusive answers
                    var success = router.DirectoryPartition.UpdateClusterRegistrationStatus(address.Grain, address.Activation, GrainDirectoryEntryStatus.Doubtful, GrainDirectoryEntryStatus.RequestedOwnership);
                    if (!success) ProtocolError(address, "unable to transition from REQUESTED_OWNERSHIP to DOUBTFUL");
                }
                else if (mcstatus == GrainDirectoryEntryStatus.RaceLoser)
                {
                    // we failed because an external request moved us to RACE_LOSER
                    var success = router.DirectoryPartition.UpdateClusterRegistrationStatus(address.Grain, address.Activation, GrainDirectoryEntryStatus.Doubtful, GrainDirectoryEntryStatus.RaceLoser);
                    if (!success) ProtocolError(address, "unable to transition from RACE_LOSER to DOUBTFUL");
                }
                else
                {
                    ProtocolError(address, "unhandled protocol state");
                }

                TrackDoubtfulGrain(address.Grain);
            }

            // remove loser activations
            foreach (var kvp in loser_activations_per_silo)
            {
                var catalog = this.grainFactory.GetSystemTarget<ICatalog>(Constants.CatalogId, kvp.Key);
                catalog.DeleteActivations(kvp.Value).Ignore();
            }
        }

        private void ProtocolError(ActivationAddress address, string msg)
        {
            logger.Error((int) ErrorCode.GlobalSingleInstance_ProtocolError, string.Format("GSIP:Req {0} {1}", address.Grain.ToString(), msg));
        }

        public void OnMultiClusterConfigurationChange(MultiClusterConfiguration next)
        {
            logger.Debug($"GSIP:M MultiClusterConfiguration {next}");
            Prod();
        }

        public void Prod()
        {
            // cancel the waiting, to proceed immediately
            runNow.Set();
        }
    }
}
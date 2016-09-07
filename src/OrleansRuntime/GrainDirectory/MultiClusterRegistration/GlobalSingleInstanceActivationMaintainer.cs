using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using Orleans.SystemTargetInterfaces;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Scheduler;
using OutcomeState = Orleans.Runtime.GrainDirectory.GlobalSingleInstanceResponseOutcome.OutcomeState;

namespace Orleans.Runtime.GrainDirectory
{
    internal class GlobalSingleInstanceActivationMaintainer : AsynchAgent
    {
        private LocalGrainDirectory router;
        private Logger logger;
        private TimeSpan period;

        internal GlobalSingleInstanceActivationMaintainer(LocalGrainDirectory router, Logger logger, GlobalConfiguration config)
        {
            this.router = router;
            this.logger = logger;
            this.period = config.GlobalSingleInstanceRetryInterval;
            logger.Verbose("GSIP:M GlobalSingleInstanceActivationMaintainer Started, Period = {0}", period);

        }

        // scanning the entire directory for doubtful activations is too slow.
        // therefore, we maintain a list of potentially doubtful activations on the side.
        // maintainer periodically takes and processes this list.
        private List<GrainId> doubtfulGrains = new List<GrainId>();
        private object lockable = new object();

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
        protected override async void Run()
        {
            var globalConfig = Silo.CurrentSilo.OrleansConfig.Globals;
            if (!globalConfig.HasMultiClusterNetwork)
                return;

            var myClusterId = globalConfig.ClusterId;

            while (router.Running)
            {
                try
                {
                    await Task.Delay(period);
                    if (!router.Running) break;

                    logger.Verbose("GSIP:M running periodic check (having waited {0})", period);

                    // examine the multicluster configuration
                    var config = Silo.CurrentSilo.LocalMultiClusterOracle.GetMultiClusterConfiguration();

                    if (config == null || !config.Clusters.Contains(myClusterId))
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

                        logger.Verbose("GSIP:M Not joined to multicluster. Make {0} owned entries doubtful {1}", ownedEntries.Count, logger.IsVerbose2 ? string.Join(",", ownedEntries.Select(s => s.Item1)) : "");

                        await router.Scheduler.QueueTask(
                            () => RunBatchedDemotion(ownedEntries),
                            router.CacheValidator.SchedulingContext
                        );
                    }
                    else
                    {
                        // we are joined to the multicluster.
                        // go through all doubtful entries and broadcast ownership requests for each

                        // take them all out of the list for processing.
                        List<GrainId> grains;
                        lock (lockable)
                        {
                            grains = doubtfulGrains;
                            doubtfulGrains = new List<GrainId>();
                        }

                        // filter
                        logger.Verbose("GSIP:M retry {0} doubtful entries {1}", grains.Count, logger.IsVerbose2 ? string.Join(",", grains) : "");

                        var remoteClusters = config.Clusters.Where(id => id != myClusterId).ToList();
                        await router.Scheduler.QueueTask(
                            () => RunBatchedActivationRequests(remoteClusters, grains),
                            router.CacheValidator.SchedulingContext
                        );
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

            return TaskDone.Done;
        }

        private async Task RunBatchedActivationRequests(List<string> remoteClusters, List<GrainId> grains)
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
                // find gateway
                var gossiporacle = Silo.CurrentSilo.LocalMultiClusterOracle;

                // send batched request
                try
                {
                    var clusterGatewayAddress = gossiporacle.GetRandomClusterGateway(remotecluster);
                    var clusterGrainDir = InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<IClusterGrainDirectory>(Constants.ClusterDirectoryServiceId, clusterGatewayAddress);
                    var r = await clusterGrainDir.ProcessActivationRequestBatch(addresses.Select(a => a.Grain).ToArray(), Silo.CurrentSilo.ClusterId);
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

            if (logger.IsVerbose)
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
                    logger.Verbose("GSIP:M batchresponse PASS:{0} FAILED:{1} FAILED(a){2}: FAILED(o){3}: FAULTED:{4}",
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

                if (logger.IsVerbose2)
                    logger.Verbose2("GSIP:M {0} Result={1}", address.Grain, outcomeDetails);

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
                var catalog = InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<ICatalog>(Constants.CatalogId, kvp.Key);
                catalog.DeleteActivations(kvp.Value).Ignore();
            }
        }

        private void ProtocolError(ActivationAddress address, string msg)
        {
            logger.Error((int) ErrorCode.GlobalSingleInstance_ProtocolError, string.Format("GSIP:Req {0} {1}", address.Grain.ToString(), msg));
        }

    }
}
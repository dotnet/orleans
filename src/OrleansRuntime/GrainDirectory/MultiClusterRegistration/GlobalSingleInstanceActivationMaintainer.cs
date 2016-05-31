using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using Orleans.SystemTargetInterfaces;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Scheduler;

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

        protected async override void Run()
        {
            while (router.Running)
            {
                try
                {
                    await Task.Delay(period);

                    logger.Verbose("GSIP:M running periodic check (having waited {0})", period);

                    var globalconfig = Silo.CurrentSilo.OrleansConfig.Globals;

                    if (!globalconfig.HasMultiClusterNetwork)
                        return;

                    var myclusterid = globalconfig.ClusterId;

                    // examine the multicluster configuration
                    var config = Silo.CurrentSilo.LocalMultiClusterOracle.GetMultiClusterConfiguration();

                    if (config == null || !config.Clusters.Contains(myclusterid))
                    {
                        // we are not joined to the cluster yet/anymore. 
                        // go through all owned entries and make them doubtful

                        var ownedEntries = router.DirectoryPartition.GetItems().Where(kp =>
                        {
                            if (!kp.Value.SingleInstance) return false;
                            var act = kp.Value.Instances.FirstOrDefault();
                            if (act.Key == null) return false;
                            if (act.Value.RegistrationStatus == MultiClusterStatus.Owned) return false;
                            return true;
                        }).Select(kp => Tuple.Create(kp.Key, kp.Value.Instances.FirstOrDefault())).ToList();

                        logger.Verbose("GSIP:M make {0} owned entries doubtful", ownedEntries.Count);

                        await router.Scheduler.QueueTask(
                            () => RunBatchedDemotion(ownedEntries),
                            router.CacheValidator.SchedulingContext
                        );
                    }
                    else
                    {
                        // we are joined to the multicluster.
                        // go through all doubtful entries and broadcast ownership requests for each

                        var doubtfulEntries = router.DirectoryPartition.GetItems().Where(kp =>
                        {
                            if (!kp.Value.SingleInstance) return false;
                            var act = kp.Value.Instances.FirstOrDefault();
                            if (act.Key == null) return false;
                            if (act.Value.RegistrationStatus != MultiClusterStatus.Doubtful) return false;
                            return true;
                        }).Select(kp => Tuple.Create(kp.Key, kp.Value.Instances.FirstOrDefault())).ToList();

                        logger.Verbose("GSIP:M retry {0} doubtful entries", doubtfulEntries.Count);
                        
                        var RemoteClusters = config.Clusters.Where(id => id != myclusterid).ToList();
                        await router.Scheduler.QueueTask(
                            () => RunBatchedActivationRequests(RemoteClusters, doubtfulEntries),
                            router.CacheValidator.SchedulingContext
                        );
                    }
              
                    // NOT DOING CACHE INVALIDATION for now. it is not required for correctness

                    //3. For "CACHED" status:
                    //3a. If the entry is removed from the cached cluster, remove the cached entry. On next grain call, the protocol should handle the scenario.


                    /*
                    //STEP 3.. (TODO: Merge step 2 and 3)?
                    var cahcedEntries =
                        allEntries.Where(t => t.Item2.ActivationStatus == MultiClusterStatus.Cached);

                    results = await router.Scheduler.QueueTask(async () =>
                    {
                        return await RunAntiEntropy(cahcedEntries.ToList());
                    }, router.CacheValidator.SchedulingContext);
                   

                    if (results != null)
                    {
                        foreach (var kvp in results)
                        {
                            var ownedbyOther = kvp.Value.FirstOrDefault(r => r.ResponseStatus == ActivationResponseStatus.FAILED &&
                                                                        r.ExistingActivationAddress != null &&
                                                                        r.Owned == true);

                            var currentActivation =
                                router.DirectoryPartition.LookUpGrain(kvp.Key).Item1.FirstOrDefault(); //this will be non null.

                            if (ownedbyOther == null)
                            {
                                //remove the cached entry.
                                //Debug.Assert(false, "Removed CACHED entry");
                                router.DirectoryPartition.RemoveActivation(kvp.Key, currentActivation.Activation, true);
                            }
                            else
                            {
                                //update the cached entry to the new OWNED cluster.
                                router.DirectoryPartition.CacheOrUpdateRemoteClusterRegistration(
                                    kvp.Key, currentActivation != null ? currentActivation.Activation : null,
                                    ownedbyOther.ExistingActivationAddress);
                            }
                        }
                    }
                    */

                }
                catch (Exception e)
                {
                    System.Diagnostics.Debug.WriteLine("Caught exception: {0}", e);
                }
            }
        }
        private Task RunBatchedDemotion(List<Tuple<GrainId, KeyValuePair<ActivationId, IActivationInfo>>> entries)
        {
            var addresses = new List<ActivationAddress>();

            foreach (var entry in entries)
                router.DirectoryPartition.UpdateClusterRegistrationStatus(entry.Item1, entry.Item2.Key, MultiClusterStatus.Doubtful, MultiClusterStatus.Owned);

            return TaskDone.Done;
        }

        private async Task RunBatchedActivationRequests(List<string> RemoteClusters, List<Tuple<GrainId, KeyValuePair<ActivationId,IActivationInfo>>> entries)
        {

            var addresses = new List<ActivationAddress>();

            foreach (var entry in entries)
            {
                // transition to requesting state
                if (router.DirectoryPartition.UpdateClusterRegistrationStatus(entry.Item1, entry.Item2.Key, MultiClusterStatus.RequestedOwnership, MultiClusterStatus.Doubtful))
                {
                    var currentActivations = router.DirectoryPartition.LookUpGrain(entry.Item1).Addresses;
                    var address = currentActivations.FirstOrDefault();
                    Debug.Assert(address != null && address.Status == MultiClusterStatus.RequestedOwnership);
                    addresses.Add(address); // TODO simplify the above code?
                }
            }

            if (addresses.Count == 0)
                return;

            var batchresponses = new List<RemoteClusterActivationResponse[]>();

            var tasks = RemoteClusters.Select(async remotecluster =>
            {
                // find gateway
                var gossiporacle = Silo.CurrentSilo.LocalMultiClusterOracle;

                // send batched request
                try
                {
                    var clusterGatewayAddress = gossiporacle.GetRandomClusterGateway(remotecluster);
                    var clusterGrainDir = InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<IClusterGrainDirectory>(Constants.ClusterDirectoryServiceId, clusterGatewayAddress);
                    var r = await clusterGrainDir.ProcessActivationRequestBatch(addresses.Select(a => a.Grain).ToArray(), Silo.CurrentSilo.ClusterId);
                    batchresponses.Add(r);
                }
                catch (Exception e)
                {
                    batchresponses.Add(
                        Enumerable.Repeat<RemoteClusterActivationResponse>(
                           new RemoteClusterActivationResponse()
                           {
                               ResponseStatus = ActivationResponseStatus.FAULTED,
                               ResponseException = e
                           }, addresses.Count).ToArray());
                }

            }).ToList();
 
            // wait for all the responses to arrive or fail
            await Task.WhenAll(tasks);

            if (logger.IsVerbose)
            foreach (var br in batchresponses)
            {
                logger.Verbose("GSIP:M batchresponse PASS:{0} FAILED:{1} FAILED(a){2}: FAILED(o){3}: FAULTED:{4}",
                    br.Count((r) => r.ResponseStatus == ActivationResponseStatus.PASS),
                    br.Count((r) => r.ResponseStatus == ActivationResponseStatus.FAILED && !r.Owned && r.ExistingActivationAddress.Address == null),
                    br.Count((r) => r.ResponseStatus == ActivationResponseStatus.FAILED && !r.Owned && r.ExistingActivationAddress.Address != null),
                    br.Count((r) => r.ResponseStatus == ActivationResponseStatus.FAILED && r.Owned),
                    br.Count((r) => r.ResponseStatus == ActivationResponseStatus.FAULTED)
                );
            }

            
            // process each address

            var loser_activations_per_silo = new Dictionary<SiloAddress, List<ActivationAddress>>();

            var deactivationtasks = new List<Task>();

            for (int i = 0; i < addresses.Count; i++)
            {
                var address = addresses[i];

                // array that holds the responses
                var responses = new RemoteClusterActivationResponse[RemoteClusters.Count];

                for (int j = 0; j < batchresponses.Count; j++)
                    responses[j] = batchresponses[j][i];

                // response processor
                var tracker = new GlobalSingleInstanceResponseTracker(responses, address.Grain);

                tracker.Notify();

                Debug.Assert(tracker.Task.IsCompleted);

                var outcome = tracker.Task.Result;

                if (logger.IsVerbose)
                    logger.Verbose("GSIP:M {0} Result={1}", address.Grain.ToString(), outcome.ToString());

                switch (outcome)
                {
                    case GlobalSingleInstanceResponseTracker.Outcome.REMOTE_OWNER:
                    case GlobalSingleInstanceResponseTracker.Outcome.REMOTE_OWNER_LIKELY:
                        {
                            // record activations that lost and need to be deactivated
                            List<ActivationAddress> losers;
                            if (!loser_activations_per_silo.TryGetValue(address.Silo, out losers))
                                loser_activations_per_silo[address.Silo] = losers = new List<ActivationAddress>();
                            losers.Add(address);

                            router.DirectoryPartition.CacheOrUpdateRemoteClusterRegistration(address.Grain, address.Activation, tracker.RemoteOwner.Address);
                            continue;
                        }
                    case GlobalSingleInstanceResponseTracker.Outcome.SUCCEED:
                        {
                            var ok = (router.DirectoryPartition.UpdateClusterRegistrationStatus(address.Grain, address.Activation, MultiClusterStatus.Owned, MultiClusterStatus.RequestedOwnership));
                            if (ok)
                                continue;
                            else
                                break;
                        }
                    case GlobalSingleInstanceResponseTracker.Outcome.INCONCLUSIVE:
                        {
                            break;
                        }
                }

                // we were not successful, reread state to determine what is going on
                var currentActivations = router.DirectoryPartition.LookUpGrain(address.Grain).Addresses;
                address = currentActivations.FirstOrDefault();
                Debug.Assert(address != null);            
                
                // in each case, go back to DOUBTFUL
                if (address.Status == MultiClusterStatus.RequestedOwnership)
                {
                    // we failed because of inconclusive answers
                    var success = router.DirectoryPartition.UpdateClusterRegistrationStatus(address.Grain, address.Activation, MultiClusterStatus.Doubtful, MultiClusterStatus.RequestedOwnership);
                    if (!success) ProtocolError(address, "unable to transition from REQUESTED_OWNERSHIP to DOUBTFUL");
                }
                else if (address.Status == MultiClusterStatus.RaceLoser)
                {
                    // we failed because an external request moved us to RACE_LOSER
                    var success = router.DirectoryPartition.UpdateClusterRegistrationStatus(address.Grain, address.Activation, MultiClusterStatus.Doubtful, MultiClusterStatus.RaceLoser);
                    if (!success) ProtocolError(address, "unable to transition from RACE_LOSER to DOUBTFUL");
                }
                else
                {
                    ProtocolError(address, "unhandled protocol state");
                }
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
            logger.Error((int)ErrorCode.GlobalSingleInstance_ProtocolError, string.Format("GSIP:R {0} {1}", address.Grain.ToString(), msg));
            Debugger.Break();
        }


    }
}
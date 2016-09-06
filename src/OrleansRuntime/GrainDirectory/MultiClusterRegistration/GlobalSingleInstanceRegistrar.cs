using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using Orleans.SystemTargetInterfaces;
using Orleans.Runtime;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// A grain registrar that coordinates the directory entries for a grain between
    /// all the clusters in the current multi-cluster configuration.
    /// It uses the global-single-instance protocol to ensure that there is eventually
    /// only a single owner for each grain. When a new grain is registered, all other clusters are
    /// contacted to see if an activation already exists. If so, a pointer to that activation is 
    /// stored in the directory and returned. Otherwise, the new activation is registered.
    /// The protocol uses special states to track the status of directory entries, as listed in 
    /// <see cref="GrainDirectoryEntryStatus"/>.
    /// </summary>
    internal class GlobalSingleInstanceRegistrar : IGrainRegistrar
    {
        private readonly int numRetries;
        private readonly Logger logger;
        private readonly GrainDirectoryPartition directoryPartition;
        private readonly GlobalSingleInstanceActivationMaintainer gsiActivationMaintainer;

        public GlobalSingleInstanceRegistrar(GrainDirectoryPartition partition, Logger logger, GlobalSingleInstanceActivationMaintainer gsiActivationMaintainer, int numRetries)
             
        {
            this.directoryPartition = partition;
            this.logger = logger;
            this.gsiActivationMaintainer = gsiActivationMaintainer;
            this.numRetries = numRetries;
        }

        public bool IsSynchronous { get { return false; } }

        public AddressAndTag Register(ActivationAddress address, bool singleActivation)
        {
            throw new InvalidOperationException();
        }

        public void Unregister(ActivationAddress address, UnregistrationCause cause)
        {
            throw new InvalidOperationException();
        }

        public void Delete(GrainId gid)
        {
            throw new InvalidOperationException();
        }

        public async Task<AddressAndTag> RegisterAsync(ActivationAddress address, bool singleActivation)
        {
            if (!singleActivation)
                throw new OrleansException("global single instance protocol is incompatible with using multiple activations");

            var myClusterId = Silo.CurrentSilo.ClusterId;

            if (myClusterId == null)
            {
                // no multicluster network. Go to owned state directly.
                return directoryPartition.AddSingleActivation(address.Grain, address.Activation, address.Silo, GrainDirectoryEntryStatus.Owned);
            }

            // examine the multicluster configuration
            var config = Silo.CurrentSilo.LocalMultiClusterOracle.GetMultiClusterConfiguration();

            if (config == null || !config.Clusters.Contains(myClusterId))
            {
                // we are not joined to the cluster yet/anymore. Go to doubtful state directly.
                gsiActivationMaintainer.TrackDoubtfulGrain(address.Grain);
                return directoryPartition.AddSingleActivation(address.Grain, address.Activation, address.Silo, GrainDirectoryEntryStatus.Doubtful);
            }

            var remoteClusters = config.Clusters.Where(id => id != myClusterId).ToList();

            // Try to go into REQUESTED_OWNERSHIP state
            var myActivation = directoryPartition.AddSingleActivation(address.Grain, address.Activation, address.Silo, GrainDirectoryEntryStatus.RequestedOwnership);

            if (!myActivation.Address.Equals(address)) 
            {
                //This implies that the registration already existed in some state? return the existing activation.
                return myActivation;
            }

            // Do request rounds until successful or we run out of retries

            int retries = numRetries;

            while (retries-- > 0)
            {
                if (logger.IsVerbose)
                    logger.Verbose("GSIP:Req {0} Round={1} Act={2}", address.Grain.ToString(), numRetries - retries, myActivation.Address.ToString());

                var responses = SendRequestRound(address, remoteClusters);

                var outcome = await responses.Task;

                if (logger.IsVerbose)
                    logger.Verbose("GSIP:End {0} Round={1} Outcome={2}", address.Grain.ToString(), numRetries - retries, responses);

                switch (outcome)
                {
                    case GlobalSingleInstanceResponseTracker.Outcome.RemoteOwner:
                    case GlobalSingleInstanceResponseTracker.Outcome.RemoteOwnerLikely:
                        {
                            directoryPartition.CacheOrUpdateRemoteClusterRegistration(address.Grain, address.Activation, responses.RemoteOwner.Address);
                            return responses.RemoteOwner;
                        }
                    case GlobalSingleInstanceResponseTracker.Outcome.Succeed:
                        {
                            if (directoryPartition.UpdateClusterRegistrationStatus(address.Grain, address.Activation, GrainDirectoryEntryStatus.Owned, GrainDirectoryEntryStatus.RequestedOwnership))
                                return myActivation;
                            else
                                break; // concurrently moved to RACE_LOSER
                        }
                    case GlobalSingleInstanceResponseTracker.Outcome.Inconclusive:
                        {
                            break;
                        }
                }

                // we were not successful, reread state to determine what is going on
                int version;
                var mcstatus = directoryPartition.TryGetActivation(address.Grain, out address, out version);

                if (mcstatus == GrainDirectoryEntryStatus.RequestedOwnership)
                {
                    // we failed because of inconclusive answers. Stay in this state for retry.
                }
                else  if (mcstatus == GrainDirectoryEntryStatus.RaceLoser)
                {
                    // we failed because an external request moved us to RACE_LOSER. Go back to REQUESTED_OWNERSHIP for retry
                    var success = directoryPartition.UpdateClusterRegistrationStatus(address.Grain, address.Activation, GrainDirectoryEntryStatus.RequestedOwnership, GrainDirectoryEntryStatus.RaceLoser);
                    if (!success) ProtocolError(address, "unable to transition from RACE_LOSER to REQUESTED_OWNERSHIP");
                    // do not wait before retrying because there is a dominant remote request active so we can probably complete quickly
                }
                else
                {
                    ProtocolError(address, "unhandled protocol state");
                }
            }

            // we are done with the quick retries. Now we go into doubtful state, which means slower retries.

            var ok = directoryPartition.UpdateClusterRegistrationStatus(address.Grain, address.Activation, GrainDirectoryEntryStatus.Doubtful, GrainDirectoryEntryStatus.RequestedOwnership);
           
            if (!ok) ProtocolError(address, "unable to transition into doubtful");

            this.gsiActivationMaintainer.TrackDoubtfulGrain(address.Grain);

            return myActivation;
        }

        private void ProtocolError(ActivationAddress address, string msg)
        {
            logger.Error((int)ErrorCode.GlobalSingleInstance_ProtocolError, string.Format("GSIP:Req {0} PROTOCOL ERROR {1}", address.Grain.ToString(), msg));
        }

        public Task UnregisterAsync(List<ActivationAddress> addresses, UnregistrationCause cause)
        {
            List<ActivationAddress> formerActivationsInThisCluster = null;

            foreach (var address in addresses)
            {
                IActivationInfo existingAct;
                bool wasRemoved;
                directoryPartition.RemoveActivation(address.Grain, address.Activation, cause, out existingAct, out wasRemoved);
                if (existingAct == null)
                {
                    logger.Verbose2("GSIP:Unr {0} {1} ignored", cause, address);
                }
                else if (!wasRemoved)
                {
                    logger.Verbose2("GSIP:Unr {0} {1} too fresh", cause, address);
                }
                else if (existingAct.RegistrationStatus == GrainDirectoryEntryStatus.Owned
                        || existingAct.RegistrationStatus == GrainDirectoryEntryStatus.Doubtful)
                {
                    logger.Verbose2("GSIP:Unr {0} {1} broadcast ({2})", cause, address, existingAct.RegistrationStatus);
                    if (formerActivationsInThisCluster == null)
                        formerActivationsInThisCluster = new List<ActivationAddress>();
                    formerActivationsInThisCluster.Add(address);
                }
                else
                {
                    logger.Verbose2("GSIP:Unr {0} {1} removed ({2})", cause, address, existingAct.RegistrationStatus);
                }
            }

            if (formerActivationsInThisCluster == null)
                return TaskDone.Done;

            // we must also remove cached references to former activations in this cluster
            // from remote clusters; thus, we broadcast the unregistration
            var myClusterId = Silo.CurrentSilo.ClusterId;

            if (myClusterId == null)
                return TaskDone.Done; // single cluster - no broadcast required

            // target clusters in current configuration, other than this one
            var remoteClusters = Silo.CurrentSilo.LocalMultiClusterOracle.GetMultiClusterConfiguration().Clusters
                .Where(id => id != myClusterId).ToList();

            var tasks = new List<Task>();
            foreach (var remoteCluster in remoteClusters)
            {
                // find gateway
                var gossipOracle = Silo.CurrentSilo.LocalMultiClusterOracle;
                var clusterGatewayAddress = gossipOracle.GetRandomClusterGateway(remoteCluster);
                if (clusterGatewayAddress != null)
                {
                    var clusterGrainDir = InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<IClusterGrainDirectory>(Constants.ClusterDirectoryServiceId, clusterGatewayAddress);

                    // try to send request

                    tasks.Add(clusterGrainDir.ProcessDeactivations(formerActivationsInThisCluster));
                }
            }
            return Task.WhenAll(tasks);
        }

        public void InvalidateCache(ActivationAddress address)
        {
            IActivationInfo existingAct;
            bool wasRemoved;
            directoryPartition.RemoveActivation(address.Grain, address.Activation, UnregistrationCause.CacheInvalidation, out existingAct, out wasRemoved);
            if (!wasRemoved)
            {
                logger.Verbose2("GSIP:Inv {0} ignored", address);
            }
            else  
            {
                logger.Verbose2("GSIP:Inv {0} removed ({1})", address, existingAct.RegistrationStatus);
            }
        }
        

        public Task DeleteAsync(GrainId gid)
        {   
            directoryPartition.RemoveGrain(gid);

            // broadcast deletion to all other clusters
            var myClusterId = Silo.CurrentSilo.ClusterId;

            if (myClusterId == null)
                return TaskDone.Done; // single cluster - no broadcast required

            // target ALL clusters, not just clusters in current configuration
            var remoteClusters = Silo.CurrentSilo.LocalMultiClusterOracle.GetActiveClusters()
                .Where(id => id != myClusterId).ToList();

            var tasks = new List<Task>();
            foreach (var remoteCluster in remoteClusters)
            {
                // find gateway
                var gossipOracle = Silo.CurrentSilo.LocalMultiClusterOracle;
                var clusterGatewayAddress = gossipOracle.GetRandomClusterGateway(remoteCluster);
                if (clusterGatewayAddress != null)
                {
                    var clusterGrainDir = InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<IClusterGrainDirectory>(Constants.ClusterDirectoryServiceId, clusterGatewayAddress);

                    // try to send request
                    tasks.Add(clusterGrainDir.ProcessDeletion(gid));
                }
            }
            return Task.WhenAll(tasks);
        }

        public GlobalSingleInstanceResponseTracker SendRequestRound(ActivationAddress address, List<string> remoteClusters)
        {
            // array that holds the responses
            var responses = new RemoteClusterActivationResponse[remoteClusters.Count];

            // response processor
            var promise = new GlobalSingleInstanceResponseTracker(responses, address.Grain, logger);

            // send all requests
            for (int i = 0; i < responses.Length; i++)
                SendRequest(address.Grain, remoteClusters[i], responses, i, promise).Ignore(); // exceptions are tracked by the promise

            return promise;
        }

        /// <summary>
        /// Send GSI protocol request to the given remote cluster
        /// </summary>
        /// <param name="remotecluster"></param>
        /// <param name="responses"></param>
        /// <param name="index"></param>
        public async Task SendRequest(GrainId grain, string remotecluster, RemoteClusterActivationResponse[] responses, int index, GlobalSingleInstanceResponseTracker responseprocessor)
        {
            try
            {
                // find gateway
                var gossiporacle = Silo.CurrentSilo.LocalMultiClusterOracle;
                var clusterGatewayAddress = gossiporacle.GetRandomClusterGateway(remotecluster);
                var clusterGrainDir = InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<IClusterGrainDirectory>(Constants.ClusterDirectoryServiceId, clusterGatewayAddress);

                // try to send request
                responses[index] = await clusterGrainDir.ProcessActivationRequest(grain, Silo.CurrentSilo.ClusterId, 0);

                responseprocessor.CheckIfDone();

            }
            catch (Exception ex)
            {
                responses[index] = new RemoteClusterActivationResponse(ActivationResponseStatus.Faulted)
                {
                    ResponseException = ex
                };

                responseprocessor.CheckIfDone();
            }
        }
    }
}

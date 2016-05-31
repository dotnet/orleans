using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using Orleans.SystemTargetInterfaces;
using Orleans.MultiCluster;

namespace Orleans.Runtime.GrainDirectory
{
    internal class GlobalSingleInstanceRegistrar : IGrainRegistrar
    {
        public GlobalSingleInstanceRegistrar(GrainDirectoryPartition partition, Logger logger)
             
        {
            this.DirectoryPartition = partition;
            this.logger = logger;
        }

        private static int NUM_RETRIES = 3;
        private Logger logger;

        private GrainDirectoryPartition DirectoryPartition;

        public bool IsSynchronous { get { return false; } }

        public virtual AddressAndTag Register(ActivationAddress address, bool singleActivation)
        {
            throw new InvalidOperationException();
        }

        public virtual void Unregister(ActivationAddress address, bool force)
        {
            throw new InvalidOperationException();
        }

        public virtual void Delete(GrainId gid)
        {
            throw new InvalidOperationException();
        }

        public async Task<AddressAndTag> RegisterAsync(ActivationAddress address, bool singleact)
        {
            var globalconfig = Silo.CurrentSilo.OrleansConfig.Globals;

            if (!singleact)
                throw new OrleansException("global single instance protocol is incompatible with using multiple activations");

            if (!globalconfig.HasMultiClusterNetwork)
            {
                // no multicluster network. Go to owned state directly.
                return DirectoryPartition.AddSingleActivation(address.Grain, address.Activation, address.Silo, MultiClusterStatus.Owned);
            }

            var myclusterid = globalconfig.ClusterId;

            // examine the multicluster configuration
            var config = Silo.CurrentSilo.LocalMultiClusterOracle.GetMultiClusterConfiguration();

            if (config == null || ! config.Clusters.Contains(myclusterid))
            {
                // we are not joined to the cluster yet/anymore. Go to doubtful state directly.
                return DirectoryPartition.AddSingleActivation(address.Grain, address.Activation, address.Silo, MultiClusterStatus.Doubtful);
            }

            var RemoteClusters = config.Clusters.Where(id => id != myclusterid).ToList();

            // Try to go into REQUESTED_OWNERSHIP state
            var MyActivation = DirectoryPartition.AddSingleActivation(address.Grain, address.Activation, address.Silo, MultiClusterStatus.RequestedOwnership);

            if (!MyActivation.Address.Equals(address)) 
            {
                //This implies that the registration already existed in some state? return the existing activation.
                return MyActivation;
            }

            // Do request rounds until successful or we run out of retries

            int retries = NUM_RETRIES;

            while (retries-- > 0)
            {
                if (logger.IsVerbose)
                    logger.Verbose("GSIP:R {0} Round={1} Act={2}", address.Grain.ToString(), NUM_RETRIES - retries, MyActivation.Address.ToString());

                var responses = SendRequestRound(address, RemoteClusters);

                var outcome = await responses.Task;

                if (logger.IsVerbose)
                    logger.Verbose("GSIP:R {0} Round={1} Result={2}", address.Grain.ToString(), NUM_RETRIES - retries, outcome.ToString());

                switch (outcome)
                {
                    case GlobalSingleInstanceResponseTracker.Outcome.REMOTE_OWNER:
                    case GlobalSingleInstanceResponseTracker.Outcome.REMOTE_OWNER_LIKELY:
                        {
                            DirectoryPartition.CacheOrUpdateRemoteClusterRegistration(address.Grain, address.Activation, responses.RemoteOwner.Address);
                            return responses.RemoteOwner;
                        }
                    case GlobalSingleInstanceResponseTracker.Outcome.SUCCEED:
                        {
                            if (DirectoryPartition.UpdateClusterRegistrationStatus(address.Grain, address.Activation, MultiClusterStatus.Owned, MultiClusterStatus.RequestedOwnership))
                                return MyActivation;
                            else
                                break; // concurrently moved to RACE_LOSER
                        }
                    case GlobalSingleInstanceResponseTracker.Outcome.INCONCLUSIVE:
                        {
                            break;
                        }
                }

                // we were not successful, reread state to determine what is going on
                var currentActivations = DirectoryPartition.LookUpGrain(address.Grain).Addresses;
                address = currentActivations.FirstOrDefault();
                Debug.Assert(address != null && address.Equals(MyActivation.Address));

                if (address.Status == MultiClusterStatus.RequestedOwnership)
                {
                    // we failed because of inconclusive answers. Stay in this state for retry.
                }
                else  if (address.Status == MultiClusterStatus.RaceLoser)
                {
                    // we failed because an external request moved us to RACE_LOSER. Go back to REQUESTED_OWNERSHIP for retry
                    var success = DirectoryPartition.UpdateClusterRegistrationStatus(address.Grain, address.Activation, MultiClusterStatus.RequestedOwnership, MultiClusterStatus.RaceLoser);
                    if (!success) ProtocolError(address, "unable to transition from RACE_LOSER to REQUESTED_OWNERSHIP");
                    // do not wait before retrying because there is a dominant remote request active so we can probably complete quickly
                }
                else
                {
                    ProtocolError(address, "unhandled protocol state");
                }
            }

            // we are done with the quick retries. Now we go into doubtful state, which means slower retries.

            var ok = DirectoryPartition.UpdateClusterRegistrationStatus(address.Grain, address.Activation, MultiClusterStatus.Doubtful, MultiClusterStatus.RequestedOwnership);
           
            if (!ok) ProtocolError(address, "unable to transition into doubtful");

            return MyActivation;
        }

        private void ProtocolError(ActivationAddress address, string msg)
        {
            logger.Error((int)ErrorCode.GlobalSingleInstance_ProtocolError, string.Format("GSIP:R {0} {1}", address.Grain.ToString(), msg));
            Debugger.Break();
        }

        public Task UnregisterAsync(ActivationAddress address, bool force)
        {
            DirectoryPartition.RemoveActivation(address.Grain, address.Activation, force);
            return TaskDone.Done;

            /*
            if (address.Status == MultiClusterStatus.Owned)
            {

                var RemoteClusters = MultiClusterUtils.GetRemoteClusters();

                List<Task> responseTasks = new List<Task>();

                //send the request to each of the cluster's gateways and wait for response. 
                foreach (var remotecluster in RemoteClusters)
                {

                    // find gateway
                    var clusterGatewayAddress = Silo.CurrentSilo.LocalGossipOracle.GetRandomClusterGateway(remotecluster);
                    var clusterGrainDir = InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<IClusterGrainDirectory>(Constants.ClusterDirectoryServiceId, clusterGatewayAddress);

                    responseTasks.Add(clusterGrainDir.InvalidateCache(address.Grain));
                }

                try
                {
                    await Task.WhenAll(responseTasks);
                }
                catch (Exception ex)
                {
                    //nothing to do. further invalidation will be handled by the periodic timer.
                }
            }
            */
        }

        public virtual Task DeleteAsync(GrainId gid)
        {
            DirectoryPartition.RemoveGrain(gid);
            return TaskDone.Done;
        }

        public GlobalSingleInstanceResponseTracker SendRequestRound(ActivationAddress address, List<string> remoteClusters)
        {
            // array that holds the responses
            var responses = new RemoteClusterActivationResponse[remoteClusters.Count];

            // response processor
            var promise = new GlobalSingleInstanceResponseTracker(responses, address.Grain);

            // send all requests
            for (int i = 0; i < responses.Length; i++)
                SendRequest(address.Grain, remoteClusters[i], responses, i, promise).Ignore(); // exceptions are tracked by the promise

            return promise;
        }

        /// <summary>
        /// Send request to the given remote cluster
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
                responses[index] = await clusterGrainDir.ProcessActivationRequest(grain, Silo.CurrentSilo.ClusterId, true);

                responseprocessor.Notify();

            }
            catch (Exception ex)
            {
                responses[index] = new RemoteClusterActivationResponse
                 {
                     ResponseStatus = ActivationResponseStatus.FAULTED,
                     ResponseException = ex
                 };

                responseprocessor.Notify();
            }
        }











    }

}

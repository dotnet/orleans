using System;
using System.Linq;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using Orleans.SystemTargetInterfaces;
using System.Collections.Generic;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// This system target provides an entry point for remote clusters to call directory functions.
    /// Since remote clusters do not have ring information about this cluster, they cannot send 
    /// requests directly to the silo with the right directory partition
    /// unless they know the activation address already. Therefore,
    /// This class serves as an intermediary that forwards the requests to the correct silo.
    /// </summary>
    internal class ClusterGrainDirectory : SystemTarget, IClusterGrainDirectory
    {
        private readonly LocalGrainDirectory router;
        private readonly string clusterId;
        private readonly Logger logger;

        public ClusterGrainDirectory(LocalGrainDirectory r, GrainId grainId, string clusterId) : base(grainId, r.MyAddress)
        {
            this.router = r;
            this.clusterId = clusterId;
            this.logger = r.Logger;
        }

        public ClusterGrainDirectory(LocalGrainDirectory r, GrainId grainId, string clusterId, bool lowPriority)
            : base(grainId, r.MyAddress, lowPriority)
        {
            this.router = r;        
            this.clusterId = clusterId;
            this.logger = r.Logger;
        }


        public async Task<RemoteClusterActivationResponse> ProcessActivationRequest(GrainId grain, string requestClusterId, int hopCount = 0)
        {
            // check if the requesting cluster id is in the current configuration view of this cluster
            // if not, reject the message.
            var multiClusterConfiguration = Runtime.Silo.CurrentSilo.LocalMultiClusterOracle?.GetMultiClusterConfiguration();
            if (multiClusterConfiguration == null || !multiClusterConfiguration.Clusters.Contains(requestClusterId))       
            {
                logger.Warn(ErrorCode.GlobalSingleInstance_WarningInvalidOrigin, 
                    "GSIP:Rsp {0} Origin={1} GSI request rejected because origin is not in MC configuration", grain.ToString(), requestClusterId);

                return new RemoteClusterActivationResponse(ActivationResponseStatus.Failed);
            }

            var forwardAddress = router.CheckIfShouldForward(grain, 0, "ProcessActivationRequest");

            // on all silos other than first, we insert a retry delay and recheck owner before forwarding
            if (hopCount > 0 && forwardAddress != null)
            {
                await Task.Delay(LocalGrainDirectory.RETRY_DELAY);
                forwardAddress = router.CheckIfShouldForward(grain, hopCount, "ProcessActivationRequest(recheck)");
            }

            if (forwardAddress == null)
            {
                return ProcessRequestLocal(grain, requestClusterId);
            }
            else
            {
                if (logger.IsVerbose2)
                    logger.Verbose("GSIP:Rsp {0} Origin={1} forward to {2}", grain.ToString(), requestClusterId, forwardAddress);

                var clusterGrainDir = InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<IClusterGrainDirectory>(Constants.ClusterDirectoryServiceId, forwardAddress);
                return await clusterGrainDir.ProcessActivationRequest(grain, requestClusterId, hopCount + 1);
            }
        }

        private RemoteClusterActivationResponse ProcessRequestLocal(GrainId grain, string requestClusterId)
        {
            RemoteClusterActivationResponse response;

            //This function will be called only on the Owner silo.
            try
            {
                ActivationAddress address;
                int version;
                GrainDirectoryEntryStatus existingActivationStatus = router.DirectoryPartition.TryGetActivation(grain, out address, out version);


                //Return appropriate protocol response, given current mc status   
                switch (existingActivationStatus)
                {
                    case GrainDirectoryEntryStatus.Invalid:
                        response = RemoteClusterActivationResponse.Pass;
                        break;

                    case GrainDirectoryEntryStatus.Owned:
                        response = new RemoteClusterActivationResponse(ActivationResponseStatus.Failed)
                        {
                            ExistingActivationAddress = new AddressAndTag()
                            {
                                Address = address,
                                VersionTag = version
                            },
                            ClusterId = clusterId,
                            Owned = true
                        };
                        break;

                    case GrainDirectoryEntryStatus.Cached:
                    case GrainDirectoryEntryStatus.RaceLoser:
                        response = RemoteClusterActivationResponse.Pass;
                        break;

                    case GrainDirectoryEntryStatus.RequestedOwnership:
                    case GrainDirectoryEntryStatus.Doubtful:
                        var iWin = MultiClusterUtils.ActivationPrecedenceFunc(grain, clusterId, requestClusterId);
                        if (iWin)
                        {
                            response = new RemoteClusterActivationResponse(ActivationResponseStatus.Failed)
                            {
                                ExistingActivationAddress = new AddressAndTag()
                                {
                                    Address = address,
                                    VersionTag = version
                                },
                                ClusterId = clusterId,
                                Owned = false
                            };
                        }
                        else
                        {
                            response = RemoteClusterActivationResponse.Pass;
                            //update own activation status to race loser.
                            if (existingActivationStatus == GrainDirectoryEntryStatus.RequestedOwnership)
                            {
                                logger.Verbose2("GSIP:Rsp {0} Origin={1} RaceLoser", grain.ToString(), requestClusterId);
                                var success = router.DirectoryPartition.UpdateClusterRegistrationStatus(grain, address.Activation, GrainDirectoryEntryStatus.RaceLoser, GrainDirectoryEntryStatus.RequestedOwnership);
                                if (!success)
                                {
                                    // there was a race. retry.
                                    logger.Verbose2("GSIP:Rsp {0} Origin={1} Retry", grain.ToString(), requestClusterId);
                                    return ProcessRequestLocal(grain, requestClusterId);
                                }
                            }
                        }
                        break;
                    default:
                        throw new InvalidOperationException("Invalid MultiClusterStatus value");

                }
            }
            catch (Exception ex)
            {
                //LOG exception
                response = new RemoteClusterActivationResponse(ActivationResponseStatus.Faulted)
                {
                    ResponseException = ex
                };
            }

            if (logger.IsVerbose)
                logger.Verbose("GSIP:Rsp {0} Origin={1} Result={2}", grain.ToString(), requestClusterId, response);

            return response;
        }

        public async Task<RemoteClusterActivationResponse[]> ProcessActivationRequestBatch(GrainId[] grains, string sendingClusterId)
        {
            var tasks = grains.Select(g => ProcessActivationRequest(g, sendingClusterId)).ToList();
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception)
            {
                // Exceptions will be observed and returned in the response
            }

            var responses = tasks.Select(responseTask =>
                responseTask.Exception == null
                    ? responseTask.Result
                    : new RemoteClusterActivationResponse(ActivationResponseStatus.Faulted) { ResponseException = responseTask.Exception })
                .ToArray();

            return responses;
        }

        /// <summary>
        /// Called by a remote cluster after it deactivates GSI grains, so this cluster can remove cached entries
        /// </summary>
        /// <returns></returns>
        public Task ProcessDeactivations(List<ActivationAddress> addresses)
        {
            // standard grain directory mechanisms for this cluster can take care of this request
            // (forwards to owning silo in this cluster as needed)
            return router.UnregisterManyAsync(addresses, UnregistrationCause.Force, 0);
        }

        /// <summary>
        /// Called by a remote cluster that wishes to eradicate all activations of a grain in all clusters
        /// </summary>
        public Task ProcessDeletion(GrainId grainId)
        {
            // standard grain directory mechanisms for this cluster can take care of this request
            // (forwards to owning silo in this cluster as needed)
            return router.DeleteGrainAsync(grainId, 0);
        }



    }
}

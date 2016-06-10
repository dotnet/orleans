using System;
using System.Linq;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using Orleans.SystemTargetInterfaces;

namespace Orleans.Runtime.GrainDirectory
{
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


        public async Task<RemoteClusterActivationResponse> ProcessActivationRequest(GrainId grain, string requestClusterId, bool withRetry = true)
        {
            // check if the requesting cluster id is in the current configuration view of this cluster
            // if not, reject the message.
            var multiClusterConfiguration = Runtime.Silo.CurrentSilo.LocalMultiClusterOracle?.GetMultiClusterConfiguration();
            if (multiClusterConfiguration == null || !multiClusterConfiguration.Clusters.Contains(requestClusterId))       
            {
                if (logger.IsVerbose)
                    logger.Verbose("GSIP:D {0} From={1} Result={2}", grain.ToString(), requestClusterId, "FAILED not in config");

                return new RemoteClusterActivationResponse { ResponseStatus = ActivationResponseStatus.Failed };
            }

            var forwardAddress = router.CheckIfShouldForward(grain, 0, "ProcessActivationRequest");

            if (forwardAddress == null)
            {
                return await ProcessRequestLocal(grain, requestClusterId);
            }
            else
            {
                if (logger.IsVerbose)
                    logger.Verbose("GSIP:D {0} Origin={1} forward to {2}", grain.ToString(), requestClusterId, forwardAddress);

                var clusterGrainDir = InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<IClusterGrainDirectory>(Constants.ClusterDirectoryServiceId, forwardAddress);
                return await clusterGrainDir.ProcessActivationRequest(grain, requestClusterId, false);
            }
        }

        private Task<RemoteClusterActivationResponse> ProcessRequestLocal(GrainId grain, string requestClusterId)
        {
            RemoteClusterActivationResponse response = new RemoteClusterActivationResponse();

            //This function will be called only on the Owner silo.
    
            //Optimize? Look in the cache first?
            //NOTE: THIS COMMENT IS FROM LOOKUP. HAS IMPLICATIONS ON "OWNED" INVARIANCE.
            //// It can happen that we cannot find the grain in our partition if there were 
            // some recent changes in the membership. Return empty list in such case (and not null) to avoid
            // NullReference exceptions in the code of invokers
            try
            {
                //var activations = await LookUp(grain, LocalGrainDirectory.NUM_RETRIES);
                //since we are the owner, we can look directly into the partition. No need to lookinto the cache.
                var localResult = router.DirectoryPartition.LookUpGrain(grain);

                if (localResult.Addresses == null)
                {
                    //If no activation found in the cluster, return response as PASS.
                    response.ResponseStatus = ActivationResponseStatus.Pass;
                }
                else
                {
                    //Find the Activation Status for the entry and return appropriate value.

                    //addresses should contain only one item since there should be only one valid instance per cluster. Hence FirstOrDefault() should work fine.
                    var addressAndTag = new AddressAndTag()
                    {
                        Address = localResult.Addresses.FirstOrDefault(),
                        VersionTag = localResult.VersionTag
                    };

                    if (addressAndTag.Address == null)
                    {
                        response.ResponseStatus = ActivationResponseStatus.Pass;
                    }
                    else
                    {
                        var existingActivationStatus = addressAndTag.Address.Status;

                        switch (existingActivationStatus)
                        {
                            case MultiClusterStatus.Owned:
                                response.ResponseStatus = ActivationResponseStatus.Failed;
                                response.ExistingActivationAddress = addressAndTag;
                                response.ClusterId = clusterId;
                                response.Owned = true;
                                break;

                            case MultiClusterStatus.Cached:
                            case MultiClusterStatus.RaceLoser:
                                response.ResponseStatus = ActivationResponseStatus.Pass;
                                break;

                            case MultiClusterStatus.RequestedOwnership:
                            case MultiClusterStatus.Doubtful:

                                var iWin = MultiClusterUtils.ActivationPrecedenceFunc(grain, clusterId,
                                    requestClusterId);
                                if (iWin)
                                {
                                    response.ResponseStatus = ActivationResponseStatus.Failed;
                                    response.ExistingActivationAddress = addressAndTag;
                                    response.ClusterId = clusterId;
                                    response.Owned = false;
                                }
                                else
                                {
                                    response.ResponseStatus = ActivationResponseStatus.Pass;
                                    //update own activation status to race loser.
                                    if (existingActivationStatus == MultiClusterStatus.RequestedOwnership)
                                    {
                                        var success = router.DirectoryPartition.UpdateClusterRegistrationStatus(grain, addressAndTag.Address.Activation, MultiClusterStatus.RaceLoser, MultiClusterStatus.RequestedOwnership);
                                        if (!success)
                                        {
                                            // there was a race. retry.
                                            return ProcessRequestLocal(grain, requestClusterId);
                                        }
                                    }
                                }
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                //LOG exception
                response.ResponseStatus = ActivationResponseStatus.Faulted;
                response.ResponseException = ex;
            }

            if (logger.IsVerbose)
                logger.Verbose("GSIP:D {0} Origin={1} Result={2}", grain.ToString(), requestClusterId, response.ToString());

            return Task.FromResult(response);
        }

        public async Task<RemoteClusterActivationResponse[]> ProcessActivationRequestBatch(GrainId[] grains, string sendingClusterId)
        {
            var responses = new RemoteClusterActivationResponse[grains.Length];

            var collectDoubtfuls = grains
                .Select(async (g,i) =>
                {
                    var response = await ProcessActivationRequest(g, sendingClusterId);
                    if (response == null)
                        response = new RemoteClusterActivationResponse()
                        {
                            ResponseStatus = ActivationResponseStatus.Faulted
                        };  
                    responses[i] = response;
                }).ToList();

            await Task.WhenAll(collectDoubtfuls);
            return responses;
        }
    }
}

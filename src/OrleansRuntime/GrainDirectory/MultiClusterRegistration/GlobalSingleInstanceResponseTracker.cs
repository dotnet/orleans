using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using Orleans.SystemTargetInterfaces;

namespace Orleans.Runtime.GrainDirectory 
{
    /// <summary>
    /// A class that encapsulates response processing logic.
    /// It is a promise that fires once it has enough responses to make a determination.
    /// </summary>
    internal class GlobalSingleInstanceResponseTracker
    {
        public enum Outcome {
            Succeed,
            RemoteOwner,
            RemoteOwnerLikely,
            Inconclusive
        }

        private readonly TaskCompletionSource<Outcome> tcs = new TaskCompletionSource<Outcome>();
        private readonly GrainId grain;
        private RemoteClusterActivationResponse[] responses;
        private Logger logger;

        public AddressAndTag RemoteOwner;
        public string RemoteOwnerCluster;

        public GlobalSingleInstanceResponseTracker(RemoteClusterActivationResponse[] responses, GrainId grain, Logger logger)
        {
            this.responses = responses;
            this.grain = grain;
            this.logger = logger;

            CheckIfDone();
        }

        /// <summary>
        /// Returns the outcome of the response aggregation
        /// </summary>
        public Task<Outcome> Task => this.tcs.Task;

        // for tracing, display outcome
        public override string ToString()
        {
            if (!this.Task.IsCompleted)
                return "pending";
            else if (Task.IsFaulted)
                return "faulted";
            else
            {
                return string.Format("[{0} {1}]",
                     Task.Result.ToString(),
                     (RemoteOwner.Address != null) ? RemoteOwner.Address.ToString() : "");
            }
        }


        /// <summary>
        /// Check responses; signal completion if we have received enough responses to determine outcome.
        /// </summary>
        public void CheckIfDone()
        {
            if (!Task.IsCompleted)
            {
                if (responses.All(res => res != null && res.ResponseStatus == ActivationResponseStatus.Pass))
                {
                   // All passed, or no other clusters exist
                   tcs.TrySetResult(Outcome.Succeed);
                   return;
                }

                var ownerResponses = responses.Where(
                        res => (res != null && res.ResponseStatus == ActivationResponseStatus.Failed && res.Owned == true)).ToList();

                if (ownerResponses.Count > 0)
                {
                    if (ownerResponses.Count > 1)
                        logger.Warn((int)ErrorCode.GlobalSingleInstance_MultipleOwners, "GSIP:Req {0} Unexpected error occured. Multiple Owner Replies.", grain);

                    RemoteOwner = ownerResponses[0].ExistingActivationAddress;
                    RemoteOwnerCluster = ownerResponses[0].ClusterId;
                    tcs.TrySetResult(Outcome.RemoteOwner);
                }

                // are all responses here or have failed?
                if (responses.All(res => res != null))
                {
                    // determine best candidate
                    var candidates = responses
                        .Where(res => (res.ResponseStatus == ActivationResponseStatus.Failed && res.ExistingActivationAddress.Address != null))
                        .ToList();

                    foreach (var res in candidates)
                    {
                        if (RemoteOwner.Address == null ||
                            MultiClusterUtils.ActivationPrecedenceFunc(grain,
                                res.ClusterId, RemoteOwnerCluster))
                        {
                            RemoteOwner = res.ExistingActivationAddress;
                            RemoteOwnerCluster = res.ClusterId;
                        }
                    }

                    if (RemoteOwner.Address != null)
                        tcs.TrySetResult(Outcome.RemoteOwnerLikely);
                    else
                        tcs.TrySetResult(Outcome.Inconclusive);
                }
            }
        }
    }
}

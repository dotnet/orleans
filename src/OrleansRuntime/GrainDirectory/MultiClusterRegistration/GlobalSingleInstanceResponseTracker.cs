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
    internal class GlobalSingleInstanceResponseTracker : TaskCompletionSource<GlobalSingleInstanceResponseTracker.Outcome> {

        public enum Outcome {
            Succeed,
            RemoteOwner,
            RemoteOwnerLikely,
            Inconclusive
        }

        private readonly GrainId grain;
        private RemoteClusterActivationResponse[] responses;

        public AddressAndTag RemoteOwner;
        public string RemoteOwnerCluster;

        public GlobalSingleInstanceResponseTracker(RemoteClusterActivationResponse[] responses, GrainId grain)
        {
            this.responses = responses;
            this.grain = grain;

            Notify();
        }

        /// <summary>
        /// Check responses and transition if warranted
        /// </summary>
        public void Notify()
        {
            if (!this.Task.IsCompleted)
            {
                if (responses.All(res => res != null && res.ResponseStatus == ActivationResponseStatus.Pass))
                {
                   // All passed, or no other clusters exist
                    TrySetResult(Outcome.Succeed);
                   return;
                }

                var ownerresponses = responses.Where(
                        res => (res != null && res.ResponseStatus == ActivationResponseStatus.Failed && res.Owned == true)).ToList();

                if (ownerresponses.Count > 0)
                {
                    Debug.Assert(ownerresponses.Count == 1);

                    //TODO find a way to actually report errors
                    // if (ownerresponses.Count > 1)
                    //     logger.Warn((int) ErrorCode.GlobalSingleInstance_MultipleOwners, "Unexpected error occured. Multiple Owner Replies.");
                    
                    RemoteOwner = ownerresponses[0].ExistingActivationAddress;
                    RemoteOwnerCluster = ownerresponses[0].ClusterId;
                    TrySetResult(Outcome.RemoteOwner);
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
                        TrySetResult(Outcome.RemoteOwnerLikely);
                    else
                        TrySetResult(Outcome.Inconclusive);
                }
            }
        }
    }
}

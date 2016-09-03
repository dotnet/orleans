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
        private readonly Task<RemoteClusterActivationResponse>[] responses;
        private Logger logger;

        public AddressAndTag RemoteOwner;
        public string RemoteOwnerCluster;

        public GlobalSingleInstanceResponseTracker(Task<RemoteClusterActivationResponse>[] responses, GrainId grain, Logger logger)
        {
            this.responses = responses;
            Debug.Assert(this.responses.All(t => t != null));
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
                return
                    $"[{Task.Result} {RemoteOwner.Address}]";
            }
        }

        /// <summary>
        /// Check responses; signal completion if we have received enough responses to determine outcome.
        /// </summary>
        private void CheckIfDone()
        {
            if (!Task.IsCompleted)
            {
                // store incomplete promises at this time (as they might be completed by the time the method finishes
                var incompletePromises = responses.Where(t => !t.IsCompleted).ToArray();
                if (incompletePromises.Length == 0 && responses.All(res => res.IsCompleted && res.Result.ResponseStatus == ActivationResponseStatus.Pass))
                {
                   // All passed, or no other clusters exist
                   tcs.TrySetResult(Outcome.Succeed);
                   return;
                }

                var ownerResponses = responses
                    .Where(t => t.IsCompleted)
                    .Select(t => t.Result)
                    .Where(res => res.ResponseStatus == ActivationResponseStatus.Failed && res.Owned == true).ToList();

                if (ownerResponses.Count > 0)
                {
                    if (ownerResponses.Count > 1)
                        logger.Warn((int)ErrorCode.GlobalSingleInstance_MultipleOwners, "GSIP:Req {0} Unexpected error occured. Multiple Owner Replies.", grain);

                    RemoteOwner = ownerResponses[0].ExistingActivationAddress;
                    RemoteOwnerCluster = ownerResponses[0].ClusterId;
                    tcs.TrySetResult(Outcome.RemoteOwner);
                    return;
                }

                // are all responses here or have failed?
                if (incompletePromises.Length == 0)
                {
                    // determine best candidate
                    var candidates = responses
                        .Select(t => t.Result)
                        .Where(res => res.ResponseStatus == ActivationResponseStatus.Failed && res.ExistingActivationAddress.Address != null)
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

                    tcs.TrySetResult(RemoteOwner.Address != null ? Outcome.RemoteOwnerLikely : Outcome.Inconclusive);
                    return;
                }

                // When any of the promises that where incomplete finishes, re-run the check
                System.Threading.Tasks.Task.WhenAny(incompletePromises).ContinueWith(t => CheckIfDone());
            }
        }
    }
}

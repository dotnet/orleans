using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using Orleans.SystemTargetInterfaces;
using OutcomeState = Orleans.Runtime.GrainDirectory.GlobalSingleInstanceResponseOutcome.OutcomeState;

namespace Orleans.Runtime.GrainDirectory 
{
    internal struct GlobalSingleInstanceResponseOutcome
    {
        public enum OutcomeState
        {
            Succeed,
            RemoteOwner,
            RemoteOwnerLikely,
            Inconclusive
        }

        public static readonly GlobalSingleInstanceResponseOutcome Succeed = new GlobalSingleInstanceResponseOutcome(OutcomeState.Succeed, default(AddressAndTag), null);

        public readonly OutcomeState State;
        public readonly AddressAndTag RemoteOwnerAddress;
        public readonly string RemoteOwnerCluster;
        public GlobalSingleInstanceResponseOutcome(OutcomeState state, AddressAndTag remoteOwnerAddress, string remoteOwnerCluster)
        {
            this.State = state;
            this.RemoteOwnerAddress = remoteOwnerAddress;
            this.RemoteOwnerCluster = remoteOwnerCluster;
        }

        public override string ToString()
        {
            return $"[{this.State} {this.RemoteOwnerAddress.Address}]";
        }
    }

    /// <summary>
    /// Utility that encapsulates Global Single Instance response processing logic.
    /// </summary>
    internal class GlobalSingleInstanceResponseTracker
    {
        private readonly TaskCompletionSource<GlobalSingleInstanceResponseOutcome> tcs = new TaskCompletionSource<GlobalSingleInstanceResponseOutcome>();
        private readonly GrainId grain;
        private readonly Task<RemoteClusterActivationResponse>[] responsePromises;
        private Logger logger;

        private GlobalSingleInstanceResponseTracker(Task<RemoteClusterActivationResponse>[] responsePromises, GrainId grain, Logger logger)
        {
            this.responsePromises = responsePromises;
            this.grain = grain;
            this.logger = logger;

            CheckIfDone();
        }

        /// <summary>
        /// Gets the outcome for a full round of responses from all the clusters.
        /// </summary>
        /// <param name="responses">Responses for a particular grain from all of the clusters in the multi-cluster network</param>
        /// <param name="grainId">The ID of the grain that we want to know its owner status</param>
        /// <param name="logger">The logger in case there is useful information to log.</param>
        /// <returns>The outcome of aggregating all of the responses.</returns>
        public static GlobalSingleInstanceResponseOutcome GetOutcome(RemoteClusterActivationResponse[] responses, GrainId grainId, Logger logger)
        {
            if (responses.Any(t => t == null)) throw new ArgumentException("All responses should have a value", nameof(responses));
            return GetOutcome(responses, grainId, logger, hasPendingResponses: false).Value;
        }

        /// <summary>
        /// Gets the outcome for a full round of responses from all the clusters.
        /// </summary>
        /// <param name="responsePromises">Promises fot the responses for a particular grain from all of the clusters in the multi-cluster network</param>
        /// <param name="grainId">The ID of the grain that we want to know its owner status</param>
        /// <param name="logger">The logger in case there is useful information to log.</param>
        /// <returns>The outcome of aggregating all of the responses. The task will complete as soon as it has enough responses to make a determination, even if not all of the clusters responded yet.</returns>
        public static Task<GlobalSingleInstanceResponseOutcome> GetOutcomeAsync(Task<RemoteClusterActivationResponse>[] responsePromises, GrainId grainId, Logger logger)
        {
            if (responsePromises.Any(t => t == null)) throw new ArgumentException("All response promises should have been initiated", nameof(responsePromises));
            var details = new GlobalSingleInstanceResponseTracker(responsePromises, grainId, logger);
            return details.Task;
        }

        /// <summary>
        /// Returns the outcome of the response aggregation
        /// </summary>
        private Task<GlobalSingleInstanceResponseOutcome> Task => this.tcs.Task;

        /// <summary>
        /// Check responses; signal completion if we have received enough responses to determine outcome.
        /// </summary>
        private void CheckIfDone()
        {
            if (!tcs.Task.IsCompleted)
            {
                // store incomplete promises at this time (as they might be completed by the time the method finishes
                var incompletePromises = new List<Task<RemoteClusterActivationResponse>>();
                var completedPromises = new List<RemoteClusterActivationResponse>();
                foreach (var promise in this.responsePromises)
                {
                    if (promise.IsCompleted)
                    {
                        completedPromises.Add(promise.Result);
                    }
                    else
                    {
                        incompletePromises.Add(promise);
                    }
                }
                var outcome = GetOutcome(completedPromises, this.grain, this.logger, incompletePromises.Count > 0);
                if (outcome.HasValue)
                {
                    tcs.TrySetResult(outcome.Value);
                }
                else
                {
                    // When any of the promises that where incomplete finishes, re-run the check
                    System.Threading.Tasks.Task.WhenAny(incompletePromises).ContinueWith(t => CheckIfDone());
                }
            }
        }

        private static GlobalSingleInstanceResponseOutcome? GetOutcome(ICollection<RemoteClusterActivationResponse> responses, GrainId grainId, Logger logger, bool hasPendingResponses)
        {
            if (!hasPendingResponses && responses.All(res => res.ResponseStatus == ActivationResponseStatus.Pass))
            {
                // All passed, or no other clusters exist
                return GlobalSingleInstanceResponseOutcome.Succeed;
            }

            var ownerResponses = responses
                .Where(res => res.ResponseStatus == ActivationResponseStatus.Failed && res.Owned == true).ToList();

            if (ownerResponses.Count > 0)
            {
                if (ownerResponses.Count > 1)
                    logger.Warn((int)ErrorCode.GlobalSingleInstance_MultipleOwners, "GSIP:Req {0} Unexpected error occured. Multiple Owner Replies.", grainId);

                return new GlobalSingleInstanceResponseOutcome(OutcomeState.RemoteOwner, ownerResponses[0].ExistingActivationAddress, ownerResponses[0].ClusterId);
            }

            // are all responses here or have failed?
            if (!hasPendingResponses)
            {
                // determine best candidate
                var candidates = responses
                    .Where(res => res.ResponseStatus == ActivationResponseStatus.Failed && res.ExistingActivationAddress.Address != null)
                    .ToList();

                AddressAndTag remoteOwner = new AddressAndTag();
                string remoteOwnerCluster = null;
                foreach (var res in candidates)
                {
                    if (remoteOwner.Address == null ||
                        MultiClusterUtils.ActivationPrecedenceFunc(grainId, res.ClusterId, remoteOwnerCluster))
                    {
                        remoteOwner = res.ExistingActivationAddress;
                        remoteOwnerCluster = res.ClusterId;
                    }
                }

                var outcome = remoteOwner.Address != null ? OutcomeState.RemoteOwnerLikely : OutcomeState.Inconclusive;
                return new GlobalSingleInstanceResponseOutcome(outcome, remoteOwner, remoteOwnerCluster);
            }

            return null;
        }
    }
}

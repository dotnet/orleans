using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using Orleans.Runtime;
using System.Collections.Generic;

namespace Orleans.SystemTargetInterfaces
{
    internal enum ActivationResponseStatus
    {
        Pass,
        Failed,
        Faulted
    }

    /// <summary>
    /// Reponse message used by Global Single Instance Protocol
    /// </summary>
    [Serializable]
    internal class RemoteClusterActivationResponse
    {
        public static readonly RemoteClusterActivationResponse Pass = new RemoteClusterActivationResponse(ActivationResponseStatus.Pass);

        public RemoteClusterActivationResponse(ActivationResponseStatus responseStatus)
        {
            this.ResponseStatus = responseStatus;
        }
        public ActivationResponseStatus ResponseStatus { get; private set; }
        public AddressAndTag ExistingActivationAddress { get; set; }
        public string ClusterId { get; set; }
        public bool Owned { get; set; }
        public Exception ResponseException { get; set; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("[");
            sb.Append(ResponseStatus.ToString());
            if (ExistingActivationAddress.Address != null) {
                sb.Append(" ");
                sb.Append(ExistingActivationAddress.Address);
                sb.Append(" ");
                sb.Append(ClusterId);
            }
            if (Owned)
            {
                sb.Append(" owned");
            }
            if (ResponseException != null)
            {
                sb.Append(" ");
                sb.Append(ResponseException.GetType().Name);
            }
            sb.Append("]");
            return sb.ToString();
        }
    }

    interface IClusterGrainDirectory : ISystemTarget
    {
        /// <summary>
        /// Called on remote clusters to process a global-single-instance round
        /// </summary>
        /// <param name="grain">the grain to process</param>
        /// <param name="requestClusterId">the id of the origin cluster</param>
        /// <param name="hopCount">how many times this request has been forwarded within the cluster</param>
        /// <returns></returns>
        Task<RemoteClusterActivationResponse> ProcessActivationRequest(
            GrainId grain,
            string requestClusterId,
            int hopCount = 0);

        /// <summary>
        /// Called on remote clusters to process a global-single-instance round
        /// </summary>
        /// <param name="grains">the grains to process</param>
        /// <param name="sendingClusterId">the id of the origin cluster</param>
        /// <returns></returns>
        Task<RemoteClusterActivationResponse[]> ProcessActivationRequestBatch(
            GrainId[] grains,
            string sendingClusterId);

        /// <summary>
        /// Called on remote clusters after deactivating a owned or doubtful grain activation,
        /// to give them the opportunity to remove the cached registration
        /// </summary>
        /// <param name="addresses">the list of activations</param>
        Task ProcessDeactivations(List<ActivationAddress> addresses);

        /// <summary>
        /// Called on remote clusters when deletion of all grain registrations is asked for.
        /// </summary>
        /// <param name="grainId"></param>
        Task ProcessDeletion(GrainId grainId);

    }
}

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    /// <summary>
    /// Remote interface to grain and activation state
    /// </summary>
    internal interface ICatalog : ISystemTarget
    {
        /// <summary>
        /// Begins deactivating the specified activations on this silo.
        /// </summary>
        /// <param name="activationAddresses">The exact activation addresses to deactivate.</param>
        /// <param name="reasonCode">The reason code for deactivation.</param>
        /// <param name="reasonText">The reason text for deactivation.</param>
        /// <param name="cancellationToken">The token which cancels the request before deactivation begins.</param>
        /// <returns>
        /// A task which completes after each matching activation has entered deactivation and no longer accepts
        /// application messages. Deactivation callbacks, directory deregistration, disposal, and removal continue
        /// asynchronously.
        /// </returns>
        [Alias("C4A56D7C")]
        Task DeleteActivations(
            List<GrainAddress> activationAddresses,
            DeactivationReasonCode reasonCode,
            string reasonText,
            CancellationToken cancellationToken = default);
    }
}

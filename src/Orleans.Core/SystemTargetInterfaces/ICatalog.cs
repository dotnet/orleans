using System.Collections.Generic;
using System.Threading.Tasks;


namespace Orleans.Runtime
{
    /// <summary>
    /// Remote interface to grain and activation state
    /// </summary>
    internal interface ICatalog : ISystemTarget
    {
        /// <summary>
        /// Delete activations from this silo
        /// </summary>
        /// <param name="activationAddresses"></param>
        /// <param name="reasonCode"></param>
        /// <param name="reasonText"></param>
        /// <returns></returns>
        Task DeleteActivations(List<GrainAddress> activationAddresses, DeactivationReasonCode reasonCode, string reasonText);
    }
}

using System.Collections.Generic;
using Orleans.GrainDirectory;

namespace Orleans.Runtime
{
    internal interface IGrainInfo
    {
        Dictionary<ActivationId, IActivationInfo> Instances { get; }
        int VersionTag { get; }
        bool SingleInstance { get; }
        bool AddActivation(ActivationId act, SiloAddress silo);
        ActivationAddress AddSingleActivation(GrainId grain, ActivationId act, SiloAddress silo, GrainDirectoryEntryStatus registrationStatus);
        bool RemoveActivation(ActivationId act, UnregistrationCause cause, out IActivationInfo entry, out bool wasRemoved);

        /// <summary>
        /// Merges two grain directory infos, returning a map of activations which must be deactivated, grouped by silo.
        /// </summary>
        /// <param name="grain"></param>
        /// <param name="other"></param>
        /// <returns>A map of activations which must be deactivated, grouped by silo.</returns>
        Dictionary<SiloAddress, List<ActivationAddress>> Merge(GrainId grain, IGrainInfo other);
        void CacheOrUpdateRemoteClusterRegistration(GrainId grain, ActivationId oldActivation, ActivationId activation, SiloAddress silo);
        bool UpdateClusterRegistrationStatus(ActivationId activationId, GrainDirectoryEntryStatus registrationStatus, GrainDirectoryEntryStatus? compareWith = null);
    }
}
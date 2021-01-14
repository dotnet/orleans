using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.GrainDirectory;

namespace Orleans.Runtime
{
    internal interface IActivationInfo
    {
        SiloAddress SiloAddress { get; }
        DateTime TimeCreated { get; }
        bool OkToRemove(UnregistrationCause cause, TimeSpan lazyDeregistrationDelay);
    }

    internal interface IGrainInfo
    {
        Dictionary<ActivationId, IActivationInfo> Instances { get; }
        int VersionTag { get; }
        bool SingleInstance { get; }
        bool AddActivation(ActivationId act, SiloAddress silo);
        ActivationAddress AddSingleActivation(GrainId grain, ActivationId act, SiloAddress silo);
        bool RemoveActivation(ActivationId act, UnregistrationCause cause, TimeSpan lazyDeregistrationDelay, out IActivationInfo entry, out bool wasRemoved);

        /// <summary>
        /// Merges two grain directory infos, returning a map of activations which must be deactivated, grouped by silo.
        /// </summary>
        /// <param name="grain"></param>
        /// <param name="other"></param>
        /// <returns>A map of activations which must be deactivated, grouped by silo.</returns>
        Dictionary<SiloAddress, List<ActivationAddress>> Merge(GrainId grain, IGrainInfo other);
    }

    /// <summary>
    /// Per-silo system interface for managing the distributed, partitioned grain-silo-activation directory.
    /// </summary>
    internal interface IRemoteGrainDirectory : ISystemTarget, IDhtGrainDirectory
    {        
        /// <summary>
        /// Records a bunch of new grain activations.
        /// This method should be called only remotely during handoff.
        /// </summary>
        /// <param name="addresses">The addresses of the grains to register</param>
        /// <param name="singleActivation">If true, use single-activation registration</param>
        /// <returns></returns>
        Task RegisterMany(List<ActivationAddress> addresses, bool singleActivation);

        /// <summary>
        /// Fetch the updated information on the given list of grains.
        /// This method should be called only remotely to refresh directory caches.
        /// </summary>
        /// <param name="grainAndETagList">list of grains and generation (version) numbers. The latter denote the versions of 
        /// the lists of activations currently held by the invoker of this method.</param>
        /// <returns>list of tuples holding a grain, generation number of the list of activations, and the list of activations. 
        /// If the generation number of the invoker matches the number of the destination, the list is null. If the destination does not
        /// hold the information on the grain, generation counter -1 is returned (and the list of activations is null)</returns>
        Task<List<Tuple<GrainId, int, List<ActivationAddress>>>> LookUpMany(List<Tuple<GrainId, int>> grainAndETagList);

        /// <summary>
        /// Removes the handed off directory partition from source silo on the destination silo.
        /// </summary>
        /// <param name="source">The address of the owner of the partition.</param>
        /// <returns></returns>
        Task RemoveHandoffPartition(SiloAddress source);

        /// <summary>
        /// Registers activations from a split partition with this directory.
        /// </summary>
        /// <param name="singleActivations">The single-activation registrations from the split partition.</param>
        /// <param name="multiActivations">The multiple-activation registrations from the split partition.</param>
        /// <returns></returns>
        Task AcceptSplitPartition(List<ActivationAddress> singleActivations, List<ActivationAddress> multiActivations);
    }
}

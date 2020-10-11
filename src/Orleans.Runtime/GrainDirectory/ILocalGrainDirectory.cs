using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    internal interface ILocalGrainDirectory : IDhtGrainDirectory
    {
        /// <summary>
        /// Starts the local portion of the directory service.
        /// </summary>
        void Start();

        /// <summary>
        /// Stops the local portion of the directory service.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1716:IdentifiersShouldNotMatchKeywords", MessageId = "Stop")]
        Task Stop(bool doOnStopHandoff);

        RemoteGrainDirectory RemoteGrainDirectory { get; }
        RemoteGrainDirectory CacheValidator { get; }

        /// <summary>
        /// Removes the record for an non-existing activation from the directory service.
        /// This is used when a request is received for an activation that cannot be found, 
        /// to lazily clean up the remote directory.
        /// The timestamp is used to prevent removing a valid entry in a possible (but unlikely)
        /// race where a request is received for a new activation before the request that causes the
        /// new activation to be created.
        /// Note that this method is a no-op if the global configuration parameter DirectoryLazyDeregistrationDelay
        /// is a zero or negative TimeSpan.
        /// <para>This method must be called from a scheduler thread.</para>
        /// </summary>
        /// <param name="address">The address of the activation to remove.</param>
        /// <param name="origin"> the silo from which the message to the non-existing activation was sent</param>
        Task UnregisterAfterNonexistingActivation(ActivationAddress address, SiloAddress origin);

        /// <summary>
        /// Fetches locally known directory information for a grain.
        /// If there is no local information, either in the cache or in this node's directory partition,
        /// then this method will return false and leave the list empty.
        /// </summary>
        /// <param name="grain">The ID of the grain to look up.</param>
        /// <param name="addresses">An output parameter that receives the list of locally-known activations of the grain.</param>
        /// <returns>True if remote addresses are complete within freshness constraint</returns>
        bool LocalLookup(GrainId grain, out AddressesAndTag addresses);

        /// <summary>
        /// Invalidates cache entry for the given activation address.
        /// This method is intended to be called whenever a directory client tries to access 
        /// an activation returned from the previous directory lookup and gets a reject from the target silo 
        /// notifying him that the activation does not exist.
        /// </summary>
        /// <param name="activation">The address of the activation that needs to be invalidated in the directory cache for the given grain.</param>
        /// <param name="invalidateDirectoryAlso">If true, on owner, invalidates directory entry that point to activations in remote clusters as well</param>
        void InvalidateCacheEntry(ActivationAddress activation, bool invalidateDirectoryAlso = false);

        /// <summary>
        /// For testing purposes only.
        /// Returns the silo that this silo thinks is the primary owner of directory information for
        /// the provided grain ID.
        /// </summary>
        /// <param name="grain"></param>
        /// <returns></returns>
        SiloAddress GetPrimaryForGrain(GrainId grain);

        /// <summary>
        /// Returns the directory information held in a local directory partition for the provided grain ID.
        /// The result will be null if no information is held.
        /// </summary>
        /// <param name="grain"></param>
        /// <returns></returns>
        AddressesAndTag GetLocalDirectoryData(GrainId grain);

        /// <summary>
        /// For testing and troubleshooting purposes only.
        /// Returns the directory information held in a local directory cache for the provided grain ID.
        /// The result will be null if no information is held.
        /// </summary>
        /// <param name="grain"></param>
        /// <returns></returns>
        List<ActivationAddress> GetLocalCacheData(GrainId grain);

        /// <summary>
        /// For determining message forwarding logic, we sometimes check if a silo is part of this cluster or not
        /// </summary>
        /// <param name="silo">the address of the silo</param>
        /// <returns>true if the silo is known to be part of this cluster</returns>
        bool IsSiloInCluster(SiloAddress silo);

        /// <summary>
        /// Sets the callback to <see cref="Catalog"/> which is called when a silo is removed from membership.
        /// </summary>
        /// <param name="catalogOnSiloRemoved">The callback.</param>
        void SetSiloRemovedCatalogCallback(Action<SiloAddress, SiloStatus> catalogOnSiloRemoved);
    }
}

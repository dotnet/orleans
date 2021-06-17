using System.Collections.Generic;

namespace Orleans.Runtime
{
    /// <summary>
    /// Authoritative local, per-silo source for information about the status of other silos.
    /// </summary>
    public interface ISiloStatusOracle
    {
        /// <summary>
        /// Current status of this silo.
        /// </summary>
        SiloStatus CurrentStatus { get; }

        /// <summary>
        /// Name of this silo.
        /// </summary>
        string SiloName { get; }

        /// <summary>
        /// Silo Address of this silo.
        /// </summary>
        SiloAddress SiloAddress { get; }

        /// <summary>
        /// Get the status of a given silo. 
        /// This method returns an approximate view on the status of a given silo. 
        /// In particular, this oracle may think the given silo is alive, while it may already have failed.
        /// If this oracle thinks the given silo is dead, it has been authoritatively told so by ISiloDirectory.
        /// </summary>
        /// <param name="siloAddress">A silo whose status we are interested in.</param>
        /// <returns>The status of a given silo.</returns>
        SiloStatus GetApproximateSiloStatus(SiloAddress siloAddress);

        /// <summary>
        /// Get the statuses of all silo. 
        /// This method returns an approximate view on the statuses of all silo.
        /// </summary>
        /// <param name="onlyActive">Include only silo who are currently considered to be active. If false, include all.</param>
        /// <returns>A list of silo statuses.</returns>
        Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false);

        /// <summary>
        /// Get the name of a silo. 
        /// Silo name is assumed to be static and does not change across restarts of the same silo.
        /// </summary>
        /// <param name="siloAddress">A silo whose name we are interested in.</param>
        /// <param name="siloName">A silo name.</param>
        /// <returns>TTrue if could return the requested name, false otherwise.</returns>
        bool TryGetSiloName(SiloAddress siloAddress, out string siloName);

        /// <summary>
        /// Determine if the current silo is valid for creating new activations on or for directory lookups.
        /// </summary>
        /// <returns>The silo so ask about.</returns>
        bool IsFunctionalDirectory(SiloAddress siloAddress);

        /// <summary>
        /// Determine if the current silo is dead.
        /// </summary>
        /// <returns>The silo so ask about.</returns>
        bool IsDeadSilo(SiloAddress silo);

        /// <summary>
        /// Subscribe to status events about all silos. 
        /// </summary>
        /// <param name="observer">An observer async interface to receive silo status notifications.</param>
        /// <returns>bool value indicating that subscription succeeded or not.</returns>
        bool SubscribeToSiloStatusEvents(ISiloStatusListener observer);

        /// <summary>
        /// UnSubscribe from status events about all silos. 
        /// </summary>
        /// <returns>bool value indicating that subscription succeeded or not.</returns>
        bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener observer);
    }
}

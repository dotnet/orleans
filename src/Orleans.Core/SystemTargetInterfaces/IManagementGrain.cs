using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    /// <summary>
    /// Interface for system management functions of silos,
    /// exposed as a grain for receiving remote requests / commands.
    /// </summary>
    public interface IManagementGrain : IGrainWithIntegerKey, IVersionManager
    {
        /// <summary>
        /// Get the list of silo hosts and statuses currently known about in this cluster.
        /// </summary>
        /// <param name="onlyActive">Whether data on just current active silos should be returned,
        /// or by default data for all current and previous silo instances [including those in Joining or Dead status].</param>
        /// <returns>The hosts and their corresponding statuses.</returns>
        Task<Dictionary<SiloAddress, SiloStatus>> GetHosts(bool onlyActive = false);

        /// <summary>
        /// Get the list of silo hosts and membership information currently known about in this cluster.
        /// </summary>
        /// <param name="onlyActive">Whether data on just current active silos should be returned,
        /// or by default data for all current and previous silo instances [including those in Joining or Dead status].</param>
        /// <returns>The host entries.</returns>
        Task<MembershipEntry[]> GetDetailedHosts(bool onlyActive = false);

        /// <summary>
        /// Perform a run of the .NET garbage collector in the specified silos.
        /// </summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <returns>A <see cref="Task"/> represesnting the work performed.</returns>
        Task ForceGarbageCollection(SiloAddress[] hostsIds);

        /// <summary>Perform a run of the Orleans activation collector in the specified silos.</summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <param name="ageLimit">Maximum idle time of activations to be collected.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task ForceActivationCollection(SiloAddress[] hostsIds, TimeSpan ageLimit);

        /// <summary>
        /// Forces activation collection.
        /// </summary>
        /// <param name="ageLimit">The age limit. Grains which have been idle for longer than this period of time will be eligible for collection.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task ForceActivationCollection(TimeSpan ageLimit);

        /// <summary>Perform a run of the silo statistics collector in the specified silos.</summary>
        /// <param name="siloAddresses">List of silos this command is to be sent to.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task ForceRuntimeStatisticsCollection(SiloAddress[] siloAddresses);

        /// <summary>
        /// Return the most recent silo runtime statistics information for the specified silos.
        /// </summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <returns>Runtime statistics from the specified hosts.</returns>
        Task<SiloRuntimeStatistics[]> GetRuntimeStatistics(SiloAddress[] hostsIds);

        /// <summary>
        /// Return the most recent grain statistics information, amalgamated across silos.
        /// </summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <returns>Simple grain statistics for the specified hosts.</returns>
        Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics(SiloAddress[] hostsIds);

        /// <summary>
        /// Return the most recent grain statistics information, amalgamated across all silos.
        /// </summary>
        /// <returns>Simple grain statistics.</returns>
        Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics();

        /// <summary>
        /// Returns the most recent detailed grain statistics information, amalgamated across silos for the specified types.
        /// </summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <param name="types">Array of grain types to filter the results with</param>
        /// <returns>Detailed grain statistics.</returns>
        Task<DetailedGrainStatistic[]> GetDetailedGrainStatistics(string[] types = null, SiloAddress[] hostsIds = null);
        /// <summary>
        /// Gets the grain activation count for a specific grain type.
        /// </summary>
        /// <param name="grainReference">The grain reference.</param>
        /// <returns>Gets the number of activations of grains with the same type as the provided grain reference.</returns>
        Task<int> GetGrainActivationCount(GrainReference grainReference);
        /// <summary>
        /// Return the total count of all current grain activations across all silos.
        /// </summary>
        /// <returns>The total number of grain activations across all silos.</returns>
        Task<int> GetTotalActivationCount();

        /// <summary>
        /// Execute a control command on the specified providers on all silos in the cluster.
        /// Commands are sent to all known providers on each silo which match both the <c>providerTypeFullName</c> AND <c>providerName</c> parameters.
        /// </summary>
        /// <remarks>
        /// Providers must implement the <c>Orleans.Providers.IControllable</c>
        /// interface in order to receive these control channel commands.
        /// </remarks>
        /// <param name="providerTypeFullName">Class full name for the provider type to send this command to.</param>
        /// <param name="providerName">Provider name to send this command to.</param>
        /// <param name="command">An id / serial number of this command.
        /// This is an opaque value to the Orleans runtime - the control protocol semantics are decided between the sender and provider.</param>
        /// <param name="arg">An opaque command argument.
        /// This is an opaque value to the Orleans runtime - the control protocol semantics are decided between the sender and provider.</param>
        /// <returns>Completion promise for this operation.</returns>
        Task<object[]> SendControlCommandToProvider(string providerTypeFullName, string providerName, int command, object arg = null);

        /// <summary>
        /// Return the <see cref="Orleans.Runtime.SiloAddress"/> where a given Grain is activated (if any).
        /// </summary>
        /// <remarks>
        /// Please note that this method does not represent a strong consistent view of the Grain Catalog.
        /// The return of this method is taken based on a last known state of the grain which may or may not be up-to-date by the time the caller receive the request.
        /// </remarks>
        /// <param name="reference">The <see cref="Orleans.Runtime.IAddressable"/> to look up.</param>
        /// <returns>The <see cref="Orleans.Runtime.SiloAddress"/> where the Grain is activated or null if not activated taken from a snapshot of the last known state of the Grain Catalog.</returns>
        ValueTask<SiloAddress> GetActivationAddress(IAddressable reference);

        /// <summary>
        /// Returns all activations of the specified grain type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>A list of all active grains of the specified type.</returns>
        ValueTask<List<GrainId>> GetActiveGrains(GrainType type);
    }
}

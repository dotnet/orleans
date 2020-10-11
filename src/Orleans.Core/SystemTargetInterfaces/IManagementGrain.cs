using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Orleans.MultiCluster;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

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
        /// <returns></returns>
        Task<Dictionary<SiloAddress, SiloStatus>> GetHosts(bool onlyActive = false);


        /// <summary>
        /// Get the list of silo hosts and membership information currently known about in this cluster.
        /// </summary>
        /// <param name="onlyActive">Whether data on just current active silos should be returned, 
        /// or by default data for all current and previous silo instances [including those in Joining or Dead status].</param>
        /// <returns></returns>
        Task<MembershipEntry[]> GetDetailedHosts(bool onlyActive = false);

        /// <summary>
        /// Perform a run of the .NET garbage collector in the specified silos.
        /// </summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <returns>Completion promise for this operation.</returns>
        Task ForceGarbageCollection(SiloAddress[] hostsIds);
        /// <summary>Perform a run of the Orleans activation collecter in the specified silos.</summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <param name="ageLimit">Maximum idle time of activations to be collected.</param>
        /// <returns>Completion promise for this operation.</returns>
        Task ForceActivationCollection(SiloAddress[] hostsIds, TimeSpan ageLimit);
        Task ForceActivationCollection(TimeSpan ageLimit);
        /// <summary>Perform a run of the silo statistics collector in the specified silos.</summary>
        /// <param name="siloAddresses">List of silos this command is to be sent to.</param>
        /// <returns>Completion promise for this operation.</returns>
        Task ForceRuntimeStatisticsCollection(SiloAddress[] siloAddresses);

        /// <summary>
        /// Return the most recent silo runtime statistics information for the specified silos.
        /// </summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <returns>Completion promise for this operation.</returns>
        Task<SiloRuntimeStatistics[]> GetRuntimeStatistics(SiloAddress[] hostsIds);

        /// <summary>
        /// Return the most recent grain statistics information, amalgamated across silos.
        /// </summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <returns>Completion promise for this operation.</returns>
        Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics(SiloAddress[] hostsIds);

        /// <summary>
        /// Return the most recent grain statistics information, amalgamated across all silos.
        /// </summary>
        /// <returns>Completion promise for this operation.</returns>
        Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics();

        /// <summary>
        /// Returns the most recent detailed grain statistics information, amalgamated across silos for the specified types.
        /// </summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <param name="types">Array of grain types to filter the results with</param>
        /// <returns></returns>
        Task<DetailedGrainStatistic[]> GetDetailedGrainStatistics(string[] types = null,SiloAddress[] hostsIds=null);

        Task<int> GetGrainActivationCount(GrainReference grainReference);
        /// <summary>
        /// Return the total count of all current grain activations across all silos.
        /// </summary>
        /// <returns>Completion promise for this operation.</returns>
        /// 
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
    }
}

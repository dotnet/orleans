using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Providers;

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
        [Alias("GetHosts")]
        Task<Dictionary<SiloAddress, SiloStatus>> GetHosts(bool onlyActive = false);

        [Alias("4C0864C2")]
        Task<Dictionary<SiloAddress, SiloStatus>> GetHosts(bool onlyActive, CancellationToken cancellationToken)
            => GetHosts(onlyActive);

        /// <summary>
        /// Get the list of silo hosts and membership information currently known about in this cluster.
        /// </summary>
        /// <param name="onlyActive">Whether data on just current active silos should be returned,
        /// or by default data for all current and previous silo instances [including those in Joining or Dead status].</param>
        /// <returns>The host entries.</returns>
        [Alias("GetDetailedHosts")]
        Task<MembershipEntry[]> GetDetailedHosts(bool onlyActive = false);

        [Alias("CC6CCBC3")]
        Task<MembershipEntry[]> GetDetailedHosts(bool onlyActive, CancellationToken cancellationToken)
            => GetDetailedHosts(onlyActive);

        /// <summary>
        /// Perform a run of the .NET garbage collector in the specified silos.
        /// </summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        [Alias("ForceGarbageCollection")]
        Task ForceGarbageCollection(SiloAddress[] hostsIds);

        [Alias("5922EB76")]
        Task ForceGarbageCollection(SiloAddress[] hostsIds, CancellationToken cancellationToken)
            => ForceGarbageCollection(hostsIds);

        /// <summary>Perform a run of the Orleans activation collector in the specified silos.</summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <param name="ageLimit">Maximum idle time of activations to be collected.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        [Alias("ForceActivationCollectionOnSilos")]
        Task ForceActivationCollection(SiloAddress[] hostsIds, TimeSpan ageLimit);

        [Alias("329F9A1B")]
        Task ForceActivationCollection(SiloAddress[] hostsIds, TimeSpan ageLimit, CancellationToken cancellationToken)
            => ForceActivationCollection(hostsIds, ageLimit);

        /// <summary>
        /// Forces activation collection.
        /// </summary>
        /// <param name="ageLimit">The age limit. Grains which have been idle for longer than this period of time will be eligible for collection.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        [Alias("ForceActivationCollection")]
        Task ForceActivationCollection(TimeSpan ageLimit);

        [Alias("54E6D1D1")]
        Task ForceActivationCollection(TimeSpan ageLimit, CancellationToken cancellationToken)
            => ForceActivationCollection(ageLimit);

        /// <summary>Perform a run of the silo statistics collector in the specified silos.</summary>
        /// <param name="siloAddresses">List of silos this command is to be sent to.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        [Alias("ForceRuntimeStatisticsCollection")]
        Task ForceRuntimeStatisticsCollection(SiloAddress[] siloAddresses);

        [Alias("B761B345")]
        Task ForceRuntimeStatisticsCollection(SiloAddress[] siloAddresses, CancellationToken cancellationToken)
            => ForceRuntimeStatisticsCollection(siloAddresses);

        /// <summary>
        /// Return the most recent silo runtime statistics information for the specified silos.
        /// </summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <returns>Runtime statistics from the specified hosts.</returns>
        [Alias("GetRuntimeStatistics")]
        Task<SiloRuntimeStatistics[]> GetRuntimeStatistics(SiloAddress[] hostsIds);

        [Alias("2D761B36")]
        Task<SiloRuntimeStatistics[]> GetRuntimeStatistics(SiloAddress[] hostsIds, CancellationToken cancellationToken)
            => GetRuntimeStatistics(hostsIds);

        /// <summary>
        /// Return the most recent grain statistics information, amalgamated across silos.
        /// </summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <returns>Simple grain statistics for the specified hosts.</returns>
        [Alias("GetSimpleGrainStatisticsOnSilos")]
        Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics(SiloAddress[] hostsIds);

        [Alias("3CFF788C")]
        Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics(SiloAddress[] hostsIds, CancellationToken cancellationToken)
            => GetSimpleGrainStatistics(hostsIds);

        /// <summary>
        /// Return the most recent grain statistics information, amalgamated across all silos.
        /// </summary>
        /// <returns>Simple grain statistics.</returns>
        [Alias("GetSimpleGrainStatistics")]
        Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics();

        [Alias("ACCE9D6A")]
        Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics(CancellationToken cancellationToken)
            => GetSimpleGrainStatistics();

        /// <summary>
        /// Returns the most recent detailed grain statistics information, amalgamated across silos for the specified types.
        /// </summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <param name="types">Array of grain types to filter the results with</param>
        /// <returns>Detailed grain statistics.</returns>
        [Alias("GetDetailedGrainStatistics")]
        Task<DetailedGrainStatistic[]> GetDetailedGrainStatistics(string[]? types = null, SiloAddress[]? hostsIds = null);

        [Alias("0A1C0D82")]
        Task<DetailedGrainStatistic[]> GetDetailedGrainStatistics(
            string[]? types,
            SiloAddress[]? hostsIds,
            CancellationToken cancellationToken)
            => GetDetailedGrainStatistics(types, hostsIds);
        /// <summary>
        /// Gets the grain activation count for a specific grain type.
        /// </summary>
        /// <param name="grainReference">The grain reference.</param>
        /// <returns>Gets the number of activations of grains with the same type as the provided grain reference.</returns>
        [Alias("GetGrainActivationCount")]
        Task<int> GetGrainActivationCount(GrainReference grainReference);

        [Alias("AEDE93F6")]
        Task<int> GetGrainActivationCount(GrainReference grainReference, CancellationToken cancellationToken)
            => GetGrainActivationCount(grainReference);
        /// <summary>
        /// Return the total count of all current grain activations across all silos.
        /// </summary>
        /// <returns>The total number of grain activations across all silos.</returns>
        [Alias("GetTotalActivationCount")]
        Task<int> GetTotalActivationCount();

        [Alias("D7365B43")]
        Task<int> GetTotalActivationCount(CancellationToken cancellationToken)
            => GetTotalActivationCount();

        /// <summary>
        /// Execute a control command on the specified providers on all silos in the cluster.
        /// Commands are sent to all known providers on each silo which match both the <c>providerTypeFullName</c> AND <c>providerName</c> parameters.
        /// </summary>
        /// <remarks>
        /// Providers must implement the <c>Orleans.Providers.IControllable</c>
        /// interface in order to receive these control channel commands.
        /// </remarks>
        /// <param name="providerName">Provider name to send this command to.</param>
        /// <param name="command">An id / serial number of this command.
        /// This is an opaque value to the Orleans runtime - the control protocol semantics are decided between the sender and provider.</param>
        /// <param name="arg">An opaque command argument.
        /// This is an opaque value to the Orleans runtime - the control protocol semantics are decided between the sender and provider.</param>
        /// <returns>Completion promise for this operation.</returns>
        [Alias("SendControlCommandToProvider")]
        public Task<object?[]> SendControlCommandToProvider<T>(string providerName, int command, object? arg = null) where T : IControllable;

        [Alias("F67965CC")]
        public Task<object?[]> SendControlCommandToProvider<T>(
            string providerName,
            int command,
            object? arg,
            CancellationToken cancellationToken) where T : IControllable
            => SendControlCommandToProvider<T>(providerName, command, arg);

        /// <summary>
        /// Return the <see cref="Orleans.Runtime.SiloAddress"/> where a given Grain is activated (if any).
        /// </summary>
        /// <remarks>
        /// Please note that this method does not represent a strong consistent view of the Grain Catalog.
        /// The return of this method is taken based on a last known state of the grain which may or may not be up-to-date by the time the caller receive the request.
        /// </remarks>
        /// <param name="reference">The <see cref="Orleans.Runtime.IAddressable"/> to look up.</param>
        /// <returns>The <see cref="Orleans.Runtime.SiloAddress"/> where the Grain is activated or null if not activated taken from a snapshot of the last known state of the Grain Catalog.</returns>
        [Alias("GetActivationAddress")]
        ValueTask<SiloAddress?> GetActivationAddress(IAddressable reference);

        [Alias("317D82B6")]
        ValueTask<SiloAddress?> GetActivationAddress(IAddressable reference, CancellationToken cancellationToken)
            => GetActivationAddress(reference);

        /// <summary>
        /// Returns all activations of the specified grain type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>A list of all active grains of the specified type.</returns>
        [Alias("GetActiveGrains")]
        ValueTask<List<GrainId>> GetActiveGrains(GrainType type);

        [Alias("3DB7923B")]
        ValueTask<List<GrainId>> GetActiveGrains(GrainType type, CancellationToken cancellationToken)
            => GetActiveGrains(type);

        /// <summary>
        /// Gets estimated grain call frequency statistics from the specified hosts.
        /// </summary>
        /// <param name="hostsIds">The hosts to request grain call frequency counts from.</param>
        /// <returns>A list of estimated grain call frequencies.</returns>
        /// <remarks>
        /// Note that this resulting collection does not necessarily contain all grain calls. It contains an estimation of the calls with the highest frequency.
        /// </remarks>
        [Alias("GetGrainCallFrequencies")]
        Task<List<GrainCallFrequency>> GetGrainCallFrequencies(SiloAddress[]? hostsIds = null);

        [Alias("0F06E027")]
        Task<List<GrainCallFrequency>> GetGrainCallFrequencies(
            SiloAddress[]? hostsIds,
            CancellationToken cancellationToken)
            => GetGrainCallFrequencies(hostsIds);

        /// <summary>
        /// For testing only. Resets grain call frequency counts on the specified hosts.
        /// </summary>
        /// <param name="hostsIds">The hosts to invoke the operation on.</param>
        /// <returns>A task representing the work performed.</returns>
        [Alias("ResetGrainCallFrequencies")]
        ValueTask ResetGrainCallFrequencies(SiloAddress[]? hostsIds = null);

        [Alias("54FE0FEC")]
        ValueTask ResetGrainCallFrequencies(SiloAddress[]? hostsIds, CancellationToken cancellationToken)
            => ResetGrainCallFrequencies(hostsIds);

        /// <summary>
        /// Instructs all gateways to drop defunct (disconnected and expired) clients.
        /// </summary>
        /// <param name="excludeRecent">If true, only clients that have been disconnected for longer than the configured client expiration time will be dropped.</param>
        /// <returns>A task representing the work performed.</returns>
        [Alias("DropDisconnectedClients")]
        Task DropDisconnectedClients(bool excludeRecent);

        [Alias("101564A8")]
        Task DropDisconnectedClients(bool excludeRecent, CancellationToken cancellationToken)
            => DropDisconnectedClients(excludeRecent);
    }

    /// <summary>
    /// Represents an estimation of the frequency calls made from a source grain to a target grain.
    /// </summary>
    [GenerateSerializer]
    [Alias("Orleans.Runtime.GrainCallFrequency")]
    [Immutable]
    public struct GrainCallFrequency
    {
        /// <summary>
        /// The source grain.
        /// </summary>
        [Id(0)]
        public GrainId SourceGrain { get; set; }

        /// <summary>
        /// The target grain.
        /// </summary>
        [Id(1)]
        public GrainId TargetGrain { get; set; }

        /// <summary>
        /// The source host.
        /// </summary>
        [Id(2)]
        public SiloAddress? SourceHost { get; set; }

        /// <summary>
        /// The target host.
        /// </summary>
        [Id(3)]
        public SiloAddress? TargetHost { get; set; }

        /// <summary>
        /// The estimated number of calls made.
        /// </summary>
        [Id(4)]
        public ulong CallCount { get; set; }
    }
}

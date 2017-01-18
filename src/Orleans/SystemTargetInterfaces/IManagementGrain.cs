using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Orleans.MultiCluster;

namespace Orleans.Runtime
{
    /// <summary>
    /// Interface for system management functions of silos, 
    /// exposed as a grain for receiving remote requests / commands.
    /// </summary>
    public interface IManagementGrain : IGrainWithIntegerKey
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
        /// Set the current log level for system runtime components.
        /// </summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <param name="traceLevel">New log level to use.</param>
        /// <returns>Completion promise for this operation.</returns>
        Task SetSystemLogLevel(SiloAddress[] hostsIds, int traceLevel);
        /// <summary>
        /// Set the current log level for application grains.
        /// </summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <param name="traceLevel">New log level to use.</param>
        /// <returns>Completion promise for this operation.</returns>
        Task SetAppLogLevel(SiloAddress[] hostsIds, int traceLevel);
        /// <summary>
        /// Set the current log level for a particular Logger, by name (with prefix matching).
        /// </summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <param name="logName">Name of the Logger (with prefix matching) to change.</param>
        /// <param name="traceLevel">New log level to use.</param>
        /// <returns>Completion promise for this operation.</returns>
        Task SetLogLevel(SiloAddress[] hostsIds, string logName, int traceLevel);

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
        /// Return the most recent grain statistics information, amalgomated across silos.
        /// </summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <returns>Completion promise for this operation.</returns>
        Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics(SiloAddress[] hostsIds);

        /// <summary>
        /// Return the most recent grain statistics information, amalgomated across all silos.
        /// </summary>
        /// <returns>Completion promise for this operation.</returns>
        Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics();

        /// <summary>
        /// Returns the most recent detailed grain statistics information, amalgomated across silos for the specified types.
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

        /// <summary>
        /// Update the configuration information dynamically. Only a subset of configuration information
        /// can be updated - will throw an error (and make no config changes) if you specify attributes
        /// or elements that cannot be changed. The configuration format is XML, in the same format
        /// as the OrleansConfiguration.xml file. The allowed elements and attributes are:
        /// <pre>
        /// &lt;OrleansConfiguration&gt;
        ///     &lt;Globals&gt;
        ///         &lt;Messaging ResponseTimeout=&quot;?&quot;/&gt;
        ///         &lt;Caching CacheSize=&quot;?&quot;/&gt;
        ///         &lt;Activation CollectionInterval=&quot;?&quot; CollectionAmount=&quot;?&quot; CollectionTotalMemoryLimit=&quot;?&quot; CollectionActivationLimit=&quot;?&quot;/&gt;
        ///         &lt;Liveness ProbeTimeout=&quot;?&quot; TableRefreshTimeout=&quot;?&quot; NumMissedProbesLimit=&quot;?&quot;/&gt;
        ///     &lt;/Globals&gt;
        ///     &lt;Defaults&gt;
        ///         &lt;LoadShedding Enabled=&quot;?&quot; LoadLimit=&quot;?&quot;/&gt;
        ///         &lt;Tracing DefaultTraceLevel=&quot;?&quot; PropagateActivityId=&quot;?&quot;&gt;
        ///             &lt;TraceLevelOverride LogPrefix=&quot;?&quot; TraceLevel=&quot;?&quot;/&gt;
        ///         &lt;/Tracing&gt;
        ///     &lt;/Defaults&gt;
        /// &lt;/OrleansConfiguration&gt;
        /// </pre>
        /// </summary>
        /// <param name="hostIds">Silos to update, or null for all silos</param>
        /// <param name="configuration">XML elements and attributes to update</param>
        /// <param name="tracing">Tracing level settings</param>
        /// <returns></returns>
        Task UpdateConfiguration(SiloAddress[] hostIds, Dictionary<string, string> configuration, Dictionary<string, string> tracing);

        /// <summary>
        /// Update the stream providers dynamically. The stream providers in the listed silos will be 
        /// updated based on the differences between its loaded stream providers and the list of providers 
        /// in the streamProviderConfigurations: If a provider in the configuration object already exists 
        /// in the silo, it will be kept as is; if a provider in the configuration object does not exist 
        /// in the silo, it will be loaded and started; if a provider that exists in silo but is not in 
        /// the configuration object, it will be stopped and removed from the silo. 
        /// </summary>
        /// <param name="hostIds">Silos to update, or null for all silos</param>
        /// <param name="streamProviderConfigurations">stream provider configurations that carries target stream providers</param>
        /// <returns></returns>
        Task UpdateStreamProviders(SiloAddress[] hostIds, IDictionary<string, ProviderCategoryConfiguration> streamProviderConfigurations);

        /// <summary>
        /// Returns an array of all the active grain types in the system
        /// </summary>
        /// <param name="hostsIds">List of silos this command is to be sent to.</param>
        /// <returns></returns>
        Task<string[]> GetActiveGrainTypes(SiloAddress[] hostsIds=null);


#region MultiCluster Management

        /// <summary>
        /// Get the current list of multicluster gateways.
        /// </summary>
        /// <returns>A list of the currently known gateways</returns>
        Task<List<IMultiClusterGatewayInfo>> GetMultiClusterGateways();

        /// <summary>
        /// Get the current multicluster configuration.
        /// </summary>
        /// <returns>The current multicluster configuration, or null if there is none</returns>
        Task<MultiClusterConfiguration> GetMultiClusterConfiguration();

        /// <summary>
        /// Contact all silos in all clusters and return silos that do not have the latest multi-cluster configuration. 
        /// If some clusters and/or silos cannot be reached, an exception is thrown.
        /// </summary>
        /// <returns>A list of silo addresses of silos that do not have the latest configuration</returns>
        Task<List<SiloAddress>> FindLaggingSilos();
 
        /// <summary>
        /// Configure the active multi-cluster, by injecting a multicluster configuration.
        /// </summary>
        /// <param name="clusters">the clusters that should be part of the active configuration</param>
        /// <param name="comment">a comment to store alongside the configuration</param>
        /// <param name="checkForLaggingSilosFirst">if true, checks that all clusters are reachable and up-to-date before injecting the new configuration</param>
        /// <returns> The task completes once information has propagated to the gossip channels</returns>
        Task<MultiClusterConfiguration> InjectMultiClusterConfiguration(IEnumerable<string> clusters, string comment = "", bool checkForLaggingSilosFirst = true);

#endregion
    }
}

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Orleans.Core;
using Orleans.Runtime.Configuration;


namespace Orleans.Runtime
{
    public interface ICorePerformanceMetrics
    {
        /// <summary>
        /// CPU utilization
        /// </summary>
        float CpuUsage { get; }

        /// <summary>
        /// Amount of memory available to processes running on the machine
        /// </summary>
        long AvailablePhysicalMemory { get; }

        /// <summary>
        /// Current memory usage
        /// </summary>
        long MemoryUsage { get; }

        /// <summary>
        /// Amount of physical memory on the machine
        /// </summary>
        long TotalPhysicalMemory { get; }

        /// <summary>
        /// the current size of the send queue (number of messages waiting to be sent). 
        /// Only captures remote messages to other silos (not including messages to the clients).
        /// </summary>
        int SendQueueLength { get; }

        /// <summary>
        /// the current size of the receive queue (number of messages that arrived to this silo and 
        /// are waiting to be dispatched). Captures both remote and local messages from other silos 
        /// as well as from the clients.
        /// </summary>
        int ReceiveQueueLength { get; }

        /// <summary>
        /// total number of remote messages sent to other silos as well as to the clients.
        /// </summary>
        long SentMessages { get; }

        /// <summary>
        /// total number of remote received messages, from other silos as well as from the clients.
        /// </summary>
        long ReceivedMessages { get; }
    }

    /// <summary>
    /// A small set of per-silo important key performance metrics
    /// </summary>
    public interface ISiloPerformanceMetrics : ICorePerformanceMetrics
    {   
        /// <summary>
        /// the current size of the receive queue (number of messages that arrived to this silo and 
        /// are waiting to be dispatched). Captures both remote and local messages from other silos 
        /// as well as from the clients.
        /// </summary>
        long RequestQueueLength { get; }

        /// <summary>
        /// number of activations on this silo
        /// </summary>
        int ActivationCount { get; }

        /// <summary>
        /// Number of activations on this silo that were used in the last 10 minutes 
        /// (Note: this number may currently not be accurate if different age limits 
        /// are used for different grain types).
        /// </summary>
        int RecentlyUsedActivationCount { get; }

        /// <summary>
        /// Number of currently connected clients
        /// </summary>
        long ClientCount { get; }

        /// <summary>
        /// whether this silo is currently overloaded and is in the load shedding mode.
        /// </summary>
        bool IsOverloaded { get; } 

        void LatchIsOverload(bool overloaded); // For testing only
        void UnlatchIsOverloaded(); // For testing only
        void LatchCpuUsage(float value); // For testing only
        void UnlatchCpuUsage(); // For testing only
    }

    /// <summary>
    /// A small set of per-Orleans-client important key performance metrics.
    /// </summary>
    public interface IClientPerformanceMetrics : ICorePerformanceMetrics
    {
        /// <summary>
        /// number of gateways that this client is currently connected to.
        /// </summary>
        long ConnectedGatewayCount { get; }
    }

    public interface ISiloMetricsDataPublisher
    {
        Task Init(string deploymentId, string storageConnectionString, SiloAddress siloAddress, string siloName, IPEndPoint gateway, string hostName);
        Task ReportMetrics(ISiloPerformanceMetrics metricsData);
    }

    public interface IConfigurableSiloMetricsDataPublisher : ISiloMetricsDataPublisher
    {
        void AddConfiguration(string deploymentId, bool isSilo, string siloName, SiloAddress address, System.Net.IPEndPoint gateway, string hostName);
    }

    public interface IClientMetricsDataPublisher
    {
        Task Init(ClientConfiguration config, IPAddress address, string clientId);
        Task ReportMetrics(IClientPerformanceMetrics metricsData);
    }

    public interface IConfigurableClientMetricsDataPublisher : IClientMetricsDataPublisher
    {
        void AddConfiguration(string deploymentId, string hostName, string clientId, System.Net.IPAddress address);
    }

    public interface IStatisticsPublisher
    {
        Task ReportStats(List<ICounter> statsCounters);
        Task Init(bool isSilo, string storageConnectionString, string deploymentId, string address, string siloName, string hostName);
    }

    public interface IConfigurableStatisticsPublisher : IStatisticsPublisher
    {
        void AddConfiguration(string deploymentId, bool isSilo, string siloName, SiloAddress address, System.Net.IPEndPoint gateway, string hostName);
    }

    /// <summary>
    /// Snapshot of current runtime statistics for a silo
    /// </summary>
    [Serializable]
    public class SiloRuntimeStatistics
    {
        /// <summary>
        /// Total number of activations in a silo.
        /// </summary>
        public int ActivationCount { get; internal set; }

        /// <summary>
        /// Number of activations in a silo that have been recently used.
        /// </summary>
        public int RecentlyUsedActivationCount { get; internal set; }

        /// <summary>
        /// The size of the request queue.
        /// </summary>
        public long RequestQueueLength { get; internal set; }

        /// <summary>
        /// The size of the sending queue.
        /// </summary>
        public int SendQueueLength { get; internal set; }

        /// <summary>
        /// The size of the receiving queue.
        /// </summary>
        public int ReceiveQueueLength { get; internal set; }

        /// <summary>
        /// The CPU utilization.
        /// </summary>
        public float CpuUsage { get; internal set; }

        /// <summary>
        /// The amount of memory available in the silo [bytes].
        /// </summary>
        public float AvailableMemory { get; internal set; }

        /// <summary>
        /// The used memory size.
        /// </summary>
        public long MemoryUsage { get; internal set; }

        /// <summary>
        /// The total physical memory available [bytes].
        /// </summary>
        public long TotalPhysicalMemory { get; internal set; }

        /// <summary>
        /// Is this silo overloaded.
        /// </summary>
        public bool IsOverloaded { get; internal set; }

        /// <summary>
        /// The number of clients currently connected to that silo.
        /// </summary>
        public long ClientCount { get; internal set; }

        public long ReceivedMessages { get; internal set; }

        public long SentMessages { get; internal set; }


        /// <summary>
        /// The DateTime when this statistics was created.
        /// </summary>
        public DateTime DateTime { get; private set; }

        internal SiloRuntimeStatistics() { }

        internal SiloRuntimeStatistics(ISiloPerformanceMetrics metrics, DateTime dateTime)
        {
            ActivationCount = metrics.ActivationCount;
            RecentlyUsedActivationCount = metrics.RecentlyUsedActivationCount;
            RequestQueueLength = metrics.RequestQueueLength;
            SendQueueLength = metrics.SendQueueLength;
            ReceiveQueueLength = metrics.ReceiveQueueLength;
            CpuUsage = metrics.CpuUsage;
            AvailableMemory = metrics.AvailablePhysicalMemory;
            MemoryUsage = metrics.MemoryUsage;
            IsOverloaded = metrics.IsOverloaded;
            ClientCount = metrics.ClientCount;
            TotalPhysicalMemory = metrics.TotalPhysicalMemory;
            ReceivedMessages = metrics.ReceivedMessages;
            SentMessages = metrics.SentMessages;
            DateTime = dateTime;
        }

        public override string ToString()
        {
            return String.Format("SiloRuntimeStatistics: ActivationCount={0} RecentlyUsedActivationCount={11} RequestQueueLength={1} SendQueueLength={2} " +
                                 "ReceiveQueueLength={3} CpuUsage={4} AvailableMemory={5} MemoryUsage={6} IsOverloaded={7} " +
                                 "ClientCount={8} TotalPhysicalMemory={9} DateTime={10}", ActivationCount, RequestQueueLength,
                                 SendQueueLength, ReceiveQueueLength, CpuUsage, AvailableMemory, MemoryUsage, IsOverloaded,
                                 ClientCount, TotalPhysicalMemory, DateTime, RecentlyUsedActivationCount);
        }
    }

    /// <summary>
    /// Snapshot of current statistics for a given grain type.
    /// </summary>
    [Serializable]
    internal class GrainStatistic
    {
        /// <summary>
        /// The type of the grain for this GrainStatistic.
        /// </summary>
        public string GrainType { get; set; }

        /// <summary>
        /// Number of grains of a this type.
        /// </summary>
        public int GrainCount { get; set; }

        /// <summary>
        /// Number of activation of a agrain of this type.
        /// </summary>
        public int ActivationCount { get; set; }

        /// <summary>
        /// Number of silos that have activations of this grain type.
        /// </summary>
        public int SiloCount { get; set; }

        /// <summary>
        /// Returns the string representatio of this GrainStatistic.
        /// </summary>
        public override string ToString()
        {
            return string.Format("GrainStatistic: GrainType={0} NumSilos={1} NumGrains={2} NumActivations={3} ", GrainType, SiloCount, GrainCount, ActivationCount);
        }
    }

    /// <summary>
    /// Simple snapshot of current statistics for a given grain type on a given silo.
    /// </summary>
    [Serializable]
    public class SimpleGrainStatistic
    { 
        /// <summary>
        /// The type of the grain for this SimpleGrainStatistic.
        /// </summary>
        public string GrainType { get; set; }

        /// <summary>
        /// The silo address for this SimpleGrainStatistic.
        /// </summary>
        public SiloAddress SiloAddress { get; set; }

        /// <summary>
        /// The number of activations of this grain type on this given silo.
        /// </summary>
        public int ActivationCount { get; set; }

        /// <summary>
        /// Returns the string representatio of this SimpleGrainStatistic.
        /// </summary>
        public override string ToString()
        {
            return string.Format("SimpleGrainStatistic: GrainType={0} Silo={1} NumActivations={2} ", GrainType, SiloAddress, ActivationCount);
        }
    }

    [Serializable]
    public class DetailedGrainStatistic
    {
        /// <summary>
        /// The type of the grain for this DetailedGrainStatistic.
        /// </summary>
        public string GrainType { get; set; }

        /// <summary>
        /// The silo address for this DetailedGrainStatistic.
        /// </summary>
        public SiloAddress SiloAddress { get; set; }

        /// <summary>
        /// Unique Id for the grain.
        /// </summary>
        public IGrainIdentity GrainIdentity { get; set; }

        /// <summary>
        /// The grains Category
        /// </summary>
        public string Category { get; set; }
    }

    [Serializable]
    internal class DetailedGrainReport
    {
        public GrainId Grain { get; set; }
        /// <summary>silo on which these statistics come from</summary>
        public SiloAddress SiloAddress { get; set; }
        /// <summary>silo on which these statistics come from</summary>
        public string SiloName { get; set; }
        /// <summary>activation addresses in the local directory cache</summary>
        public List<ActivationAddress> LocalCacheActivationAddresses { get; set; }
        /// <summary>activation addresses in the local directory.</summary>
        public List<ActivationAddress> LocalDirectoryActivationAddresses { get; set; }
        /// <summary>primary silo for this grain</summary>
        public SiloAddress PrimaryForGrain { get; set; }
        /// <summary>the name of the class that implements this grain.</summary>
        public string GrainClassTypeName { get; set; }
        /// <summary>activations on this silo</summary>
        public List<string> LocalActivations { get; set; }

        public override string ToString()
        {
            return string.Format(Environment.NewLine 
                + "**DetailedGrainReport for grain {0} from silo {1} SiloAddress={2}" + Environment.NewLine 
                + "   LocalCacheActivationAddresses={3}" + Environment.NewLine
                + "   LocalDirectoryActivationAddresses={4}"  + Environment.NewLine
                + "   PrimaryForGrain={5}" + Environment.NewLine 
                + "   GrainClassTypeName={6}" + Environment.NewLine
                + "   LocalActivations:" + Environment.NewLine
                + "{7}." + Environment.NewLine,
                    Grain.ToDetailedString(),                                   // {0}
                    SiloName,                                                   // {1}
                    SiloAddress.ToLongString(),                                 // {2}
                    Utils.EnumerableToString(LocalCacheActivationAddresses),    // {3}
                    Utils.EnumerableToString(LocalDirectoryActivationAddresses),// {4}
                    PrimaryForGrain,                                            // {5}
                    GrainClassTypeName,                                         // {6}
                    Utils.EnumerableToString(LocalActivations,                  // {7}
                        str => string.Format("      {0}", str), "\n"));
        }
    }
}

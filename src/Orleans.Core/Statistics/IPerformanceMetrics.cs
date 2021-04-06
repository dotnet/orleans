using System;
using System.Collections.Generic;

using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Core;
using Orleans.Statistics;

namespace Orleans.Runtime
{
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
        public float? CpuUsage { get; internal set; }

        /// <summary>
        /// The amount of memory available in the silo [bytes].
        /// </summary>
        public float? AvailableMemory { get; internal set; }

        /// <summary>
        /// The used memory size.
        /// </summary>
        public long? MemoryUsage { get; internal set; }

        /// <summary>
        /// The total physical memory available [bytes].
        /// </summary>
        public long? TotalPhysicalMemory { get; internal set; }

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

        internal SiloRuntimeStatistics(
            IMessageCenter messageCenter,
            int activationCount,
            int recentlyUsedActivationCount,
            IAppEnvironmentStatistics appEnvironmentStatistics,
            IHostEnvironmentStatistics hostEnvironmentStatistics,
            IOptions<LoadSheddingOptions> loadSheddingOptions,
            DateTime dateTime)
        {
            ActivationCount = activationCount;
            RecentlyUsedActivationCount = recentlyUsedActivationCount;
            SendQueueLength = messageCenter.SendQueueLength;
            CpuUsage = hostEnvironmentStatistics.CpuUsage;
            AvailableMemory = hostEnvironmentStatistics.AvailableMemory;
            MemoryUsage = appEnvironmentStatistics.MemoryUsage;
            IsOverloaded = loadSheddingOptions.Value.LoadSheddingEnabled && this.CpuUsage > loadSheddingOptions.Value.LoadSheddingLimit;
            ClientCount = MessagingStatisticsGroup.ConnectedClientCount.GetCurrentValue();
            TotalPhysicalMemory = hostEnvironmentStatistics.TotalPhysicalMemory;
            ReceivedMessages = MessagingStatisticsGroup.MessagesReceived.GetCurrentValue();
            SentMessages = MessagingStatisticsGroup.MessagesSentTotal.GetCurrentValue();
            DateTime = dateTime;
        }

        public override string ToString()
        {
            return
                "SiloRuntimeStatistics: "
                + $"ActivationCount={ActivationCount} " 
                + $"RecentlyUsedActivationCount={RecentlyUsedActivationCount} "
                + $"SendQueueLength={SendQueueLength} "
                + $"CpuUsage={CpuUsage} "
                + $"AvailableMemory={AvailableMemory} "
                + $"MemoryUsage={MemoryUsage} "
                + $"IsOverloaded={IsOverloaded} "
                + $"ClientCount={ClientCount} "
                + $"TotalPhysicalMemory={TotalPhysicalMemory} "
                + $"DateTime={DateTime}";
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
        /// Number of activation of a grain of this type.
        /// </summary>
        public int ActivationCount { get; set; }

        /// <summary>
        /// Number of silos that have activations of this grain type.
        /// </summary>
        public int SiloCount { get; set; }

        /// <summary>
        /// Returns the string representation of this GrainStatistic.
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
        /// Returns the string representation of this SimpleGrainStatistic.
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
        public GrainId GrainId { get; set; }

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
        public ActivationAddress LocalCacheActivationAddress { get; set; }
        /// <summary>activation addresses in the local directory.</summary>
        public ActivationAddress LocalDirectoryActivationAddress { get; set; }
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
                    Grain.ToString(),                                   // {0}
                    SiloName,                                                   // {1}
                    SiloAddress.ToLongString(),                                 // {2}
                    LocalCacheActivationAddress,    // {3}
                    LocalDirectoryActivationAddress,// {4}
                    PrimaryForGrain,                                            // {5}
                    GrainClassTypeName,                                         // {6}
                    Utils.EnumerableToString(LocalActivations,                  // {7}
                        str => string.Format("      {0}", str), "\n"));
        }
    }
}

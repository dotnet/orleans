using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Statistics;

namespace Orleans.Runtime
{
    /// <summary>
    /// Snapshot of current runtime statistics for a silo
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class SiloRuntimeStatistics
    {
        /// <summary>
        /// Total number of activations in a silo.
        /// </summary>
        [Id(1)]
        public int ActivationCount { get; internal set; }

        /// <summary>
        /// Number of activations in a silo that have been recently used.
        /// </summary>
        [Id(2)]
        public int RecentlyUsedActivationCount { get; internal set; }

        /// <summary>
        /// The CPU utilization.
        /// </summary>
        [Id(3)]
        public float? CpuUsage { get; internal set; }

        /// <summary>
        /// The amount of memory available in the silo [bytes].
        /// </summary>
        [Id(4)]
        public float? AvailableMemory { get; internal set; }

        /// <summary>
        /// The used memory size.
        /// </summary>
        [Id(5)]
        public long? MemoryUsage { get; internal set; }

        /// <summary>
        /// The total physical memory available [bytes].
        /// </summary>
        [Id(6)]
        public long? TotalPhysicalMemory { get; internal set; }

        /// <summary>
        /// Is this silo overloaded.
        /// </summary>
        [Id(7)]
        public bool IsOverloaded { get; internal set; }

        /// <summary>
        /// The number of clients currently connected to that silo.
        /// </summary>
        [Id(8)]
        public long ClientCount { get; internal set; }

        [Id(9)]
        public long ReceivedMessages { get; internal set; }

        [Id(10)]
        public long SentMessages { get; internal set; }


        /// <summary>
        /// The DateTime when this statistics was created.
        /// </summary>
        [Id(11)]
        public DateTime DateTime { get; private set; }

        internal SiloRuntimeStatistics() { }

        internal SiloRuntimeStatistics(
            int activationCount,
            int recentlyUsedActivationCount,
            IAppEnvironmentStatistics appEnvironmentStatistics,
            IHostEnvironmentStatistics hostEnvironmentStatistics,
            IOptions<LoadSheddingOptions> loadSheddingOptions,
            DateTime dateTime)
        {
            ActivationCount = activationCount;
            RecentlyUsedActivationCount = recentlyUsedActivationCount;
            CpuUsage = hostEnvironmentStatistics.CpuUsage;
            AvailableMemory = hostEnvironmentStatistics.AvailableMemory;
            MemoryUsage = appEnvironmentStatistics.MemoryUsage;
            IsOverloaded = loadSheddingOptions.Value.LoadSheddingEnabled && (this.CpuUsage ?? 0) > loadSheddingOptions.Value.LoadSheddingLimit;
            ClientCount = SiloRuntimeMetricsListener.ConnectedClientCount;
            TotalPhysicalMemory = hostEnvironmentStatistics.TotalPhysicalMemory;
            ReceivedMessages = SiloRuntimeMetricsListener.MessageReceivedTotal;
            SentMessages = SiloRuntimeMetricsListener.MessageSentTotal;
            DateTime = dateTime;
        }

        public override string ToString() => @$"SiloRuntimeStatistics: ActivationCount={ActivationCount} RecentlyUsedActivationCount={RecentlyUsedActivationCount
            } CpuUsage={CpuUsage?.ToString() ?? "<unset>"} AvailableMemory={AvailableMemory} MemoryUsage={MemoryUsage} IsOverloaded={IsOverloaded
            } ClientCount={ClientCount} TotalPhysicalMemory={TotalPhysicalMemory} DateTime={DateTime}";
    }

    /// <summary>
    /// Simple snapshot of current statistics for a given grain type on a given silo.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class SimpleGrainStatistic
    {
        /// <summary>
        /// The type of the grain for this SimpleGrainStatistic.
        /// </summary>
        [Id(1)]
        public string GrainType { get; set; }

        /// <summary>
        /// The silo address for this SimpleGrainStatistic.
        /// </summary>
        [Id(2)]
        public SiloAddress SiloAddress { get; set; }

        /// <summary>
        /// The number of activations of this grain type on this given silo.
        /// </summary>
        [Id(3)]
        public int ActivationCount { get; set; }

        /// <summary>
        /// Returns the string representation of this SimpleGrainStatistic.
        /// </summary>
        public override string ToString() => $"SimpleGrainStatistic: GrainType={GrainType} Silo={SiloAddress} NumActivations={ActivationCount} ";
    }

    [Serializable]
    [GenerateSerializer]
    public class DetailedGrainStatistic
    {
        /// <summary>
        /// The type of the grain for this DetailedGrainStatistic.
        /// </summary>
        [Id(1)]
        public string GrainType { get; set; }

        /// <summary>
        /// The silo address for this DetailedGrainStatistic.
        /// </summary>
        [Id(2)]
        public SiloAddress SiloAddress { get; set; }

        /// <summary>
        /// Unique Id for the grain.
        /// </summary>
        [Id(3)]
        public GrainId GrainId { get; set; }

        /// <summary>
        /// The grains Category
        /// </summary>
        [Id(4)]
        public string Category { get; set; }
    }

    [Serializable]
    [GenerateSerializer]
    internal sealed class DetailedGrainReport
    {
        [Id(1)]
        public GrainId Grain { get; set; }

        /// <summary>silo on which these statistics come from</summary>
        [Id(2)]
        public SiloAddress SiloAddress { get; set; }

        /// <summary>silo on which these statistics come from</summary>
        [Id(3)]
        public string SiloName { get; set; }

        /// <summary>activation addresses in the local directory cache</summary>
        [Id(4)]
        public GrainAddress LocalCacheActivationAddress { get; set; }

        /// <summary>activation addresses in the local directory.</summary>
        [Id(5)]
        public GrainAddress LocalDirectoryActivationAddress { get; set; }

        /// <summary>primary silo for this grain</summary>
        [Id(6)]
        public SiloAddress PrimaryForGrain { get; set; }

        /// <summary>the name of the class that implements this grain.</summary>
        [Id(7)]
        public string GrainClassTypeName { get; set; }

        /// <summary>activation on this silo</summary>
        [Id(8)]
        public string LocalActivation { get; set; }

        public override string ToString() => @$"{Environment.NewLine
            }**DetailedGrainReport for grain {Grain} from silo {SiloName} SiloAddress={SiloAddress}{Environment.NewLine
            }   LocalCacheActivationAddresses={LocalCacheActivationAddress}{Environment.NewLine
            }   LocalDirectoryActivationAddresses={LocalDirectoryActivationAddress}{Environment.NewLine
            }   PrimaryForGrain={PrimaryForGrain}{Environment.NewLine
            }   GrainClassTypeName={GrainClassTypeName}{Environment.NewLine
            }   LocalActivation:{Environment.NewLine
            }{LocalActivation}.{Environment.NewLine}";
    }
}

using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Core.Messaging;
using Orleans.Statistics;

namespace Orleans.Runtime
{
    /// <summary>
    /// Snapshot of current runtime statistics for a silo
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    public sealed class SiloRuntimeStatistics
    {
        /// <summary>
        /// Total number of activations in a silo.
        /// </summary>
        [Id(0)]
        public int ActivationCount { get; }

        /// <summary>
        /// Number of activations in a silo that have been recently used.
        /// </summary>
        [Id(1)]
        public int RecentlyUsedActivationCount { get; }

        /// <summary>
        /// The CPU utilization.
        /// </summary>
        [Id(2), Obsolete($"The will be removed, use {nameof(EnvironmentStatistics)}.{nameof(EnvironmentStatistics.CpuUsagePercentage)} instead.", error: false)]
        public float? CpuUsage { get; }

        /// <summary>
        /// The amount of memory available in the silo [bytes].
        /// </summary>
        [Id(3), Obsolete($"The will be removed, use {nameof(EnvironmentStatistics)}.{nameof(EnvironmentStatistics.AvailableMemoryBytes)} instead.", error: false)]
        public float? AvailableMemory { get; }

        /// <summary>
        /// The used memory size.
        /// </summary>
        [Id(4), Obsolete($"The will be removed, use {nameof(EnvironmentStatistics)}.{nameof(EnvironmentStatistics.MemoryUsageBytes)} instead.", error: false)]
        public long? MemoryUsage { get; }

        /// <summary>
        /// The total physical memory available [bytes].
        /// </summary>
        [Id(5), Obsolete($"The will be removed, use {nameof(EnvironmentStatistics)}.{nameof(EnvironmentStatistics.MaximumAvailableMemoryBytes)} instead.", error: false)]
        public long? TotalPhysicalMemory { get; }

        /// <summary>
        /// Is this silo overloaded.
        /// </summary>
        [Id(6)]
        public bool IsOverloaded { get; }

        /// <summary>
        /// The number of clients currently connected to that silo.
        /// </summary>
        [Id(7)]
        public long ClientCount { get; }

        /// <summary>
        /// The number of messages received by that silo.
        /// </summary>
        [Id(8)]
        public long ReceivedMessages { get; }

        /// <summary>
        /// The number of messages sent by that silo.
        /// </summary>
        [Id(9)]
        public long SentMessages { get; }

        /// <summary>
        /// The DateTime when this statistics was created.
        /// </summary>
        [Id(10)]
        public DateTime DateTime { get; }

        [Id(11)]
        public EnvironmentStatistics EnvironmentStatistics { get; }

        internal SiloRuntimeStatistics(
            int activationCount,
            int recentlyUsedActivationCount,
            IEnvironmentStatisticsProvider environmentStatisticsProvider,
            IOptions<LoadSheddingOptions> loadSheddingOptions,
            DateTime dateTime)
        {
            ActivationCount = activationCount;
            RecentlyUsedActivationCount = recentlyUsedActivationCount;
            ClientCount = SiloRuntimeMetricsListener.ConnectedClientCount;      
            ReceivedMessages = SiloRuntimeMetricsListener.MessageReceivedTotal;
            SentMessages = SiloRuntimeMetricsListener.MessageSentTotal;
            DateTime = dateTime;

            var statistics = environmentStatisticsProvider.GetEnvironmentStatistics();

            EnvironmentStatistics = statistics;
            IsOverloaded = loadSheddingOptions.Value.LoadSheddingEnabled && OverloadDetectionLogic.IsOverloaded(ref statistics, loadSheddingOptions.Value);

#pragma warning disable 618
            CpuUsage = statistics.CpuUsagePercentage;
            MemoryUsage = statistics.MemoryUsageBytes;
            AvailableMemory = statistics.AvailableMemoryBytes;
            TotalPhysicalMemory = statistics.MaximumAvailableMemoryBytes;
#pragma warning restore 618
        }

        public override string ToString() => @$"SiloRuntimeStatistics: ActivationCount={ActivationCount} RecentlyUsedActivationCount={RecentlyUsedActivationCount
            } CpuUsagePercentage={EnvironmentStatistics.CpuUsagePercentage} MemoryUsageBytes={EnvironmentStatistics.MemoryUsageBytes
            } AvailableMemory={EnvironmentStatistics.AvailableMemoryBytes} MaximumAvailableMemoryBytes={EnvironmentStatistics.MaximumAvailableMemoryBytes
            } IsOverloaded={IsOverloaded} ClientCount={ClientCount} ReceivedMessages={ReceivedMessages} SentMessages={SentMessages} DateTime={DateTime}";
    }

    /// <summary>
    /// Simple snapshot of current statistics for a given grain type on a given silo.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    public sealed class SimpleGrainStatistic
    {
        /// <summary>
        /// The type of the grain for this SimpleGrainStatistic.
        /// </summary>
        [Id(0)]
        public string GrainType { get; init; }

        /// <summary>
        /// The silo address for this SimpleGrainStatistic.
        /// </summary>
        [Id(1)]
        public SiloAddress SiloAddress { get; init; }

        /// <summary>
        /// The number of activations of this grain type on this given silo.
        /// </summary>
        [Id(2)]
        public int ActivationCount { get; init; }

        /// <summary>
        /// Returns the string representation of this SimpleGrainStatistic.
        /// </summary>
        public override string ToString() => $"SimpleGrainStatistic: GrainType={GrainType} Silo={SiloAddress} NumActivations={ActivationCount} ";
    }

    [Serializable, GenerateSerializer, Immutable]
    public sealed class DetailedGrainStatistic
    {
        /// <summary>
        /// The type of the grain for this DetailedGrainStatistic.
        /// </summary>
        [Id(0)]
        public string GrainType { get; init; }

        /// <summary>
        /// The silo address for this DetailedGrainStatistic.
        /// </summary>
        [Id(1)]
        public SiloAddress SiloAddress { get; init; }

        /// <summary>
        /// Unique Id for the grain.
        /// </summary>
        [Id(2)]
        public GrainId GrainId { get; init; }
    }

    [Serializable, GenerateSerializer, Immutable]
    internal sealed class DetailedGrainReport
    {
        [Id(0)]
        public GrainId Grain { get; init; }

        /// <summary>silo on which these statistics come from</summary>
        [Id(1)]
        public SiloAddress SiloAddress { get; init; }

        /// <summary>silo on which these statistics come from</summary>
        [Id(2)]
        public string SiloName { get; init; }

        /// <summary>activation addresses in the local directory cache</summary>
        [Id(3)]
        public GrainAddress LocalCacheActivationAddress { get; init; }

        /// <summary>activation addresses in the local directory.</summary>
        [Id(4)]
        public GrainAddress LocalDirectoryActivationAddress { get; init; }

        /// <summary>primary silo for this grain</summary>
        [Id(5)]
        public SiloAddress PrimaryForGrain { get; init; }

        /// <summary>the name of the class that implements this grain.</summary>
        [Id(6)]
        public string GrainClassTypeName { get; init; }

        /// <summary>activation on this silo</summary>
        [Id(7)]
        public string LocalActivation { get; init; }

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


using System;
using Orleans.Configuration;
using Orleans.Statistics;

namespace Orleans.Streams
{
    /// <summary>
    /// Flow control triggered by silo load shedding.
    /// This is an all-or-nothing trigger which will request <see cref="int.MaxValue"/>, or <c>0</c>.
    /// </summary>
    public class LoadShedQueueFlowController : IQueueFlowController
    {
        private readonly LoadSheddingOptions options;
        private readonly double loadSheddingLimit;
        private readonly IEnvironmentStatisticsProvider environmentStatisticsProvider;

        /// <summary>
        /// Creates a flow controller triggered when the CPU reaches a percentage of the cluster load shedding limit.
        /// This is intended to reduce queue read rate prior to causing the silo to shed load.
        /// Note:  Triggered only when load shedding is enabled.
        /// </summary>
        /// <param name="options">The silo statistics options.</param>
        /// <param name="percentOfSiloSheddingLimit">Percentage of load shed limit which triggers a reduction of queue read rate.</param>
        /// <param name="environmentStatisticsProvider">The silo environment statistics.</param>
        /// <returns>The flow controller.</returns>
        public static IQueueFlowController CreateAsPercentOfLoadSheddingLimit(LoadSheddingOptions options, IEnvironmentStatisticsProvider environmentStatisticsProvider, int percentOfSiloSheddingLimit = LoadSheddingOptions.DefaultCpuThreshold)
        {
            if (percentOfSiloSheddingLimit < 0.0 || percentOfSiloSheddingLimit > 100.0) throw new ArgumentOutOfRangeException(nameof(percentOfSiloSheddingLimit), "Percent value must be between 0-100");
            // Start shedding before silo reaches shedding limit.
            return new LoadShedQueueFlowController((int)(options.CpuThreshold * (percentOfSiloSheddingLimit / 100.0)), options, environmentStatisticsProvider);
        }

        /// <summary>
        /// Creates a flow controller triggered when the CPU reaches the specified limit.
        /// Note:  Triggered only when load shedding is enabled.
        /// </summary>
        /// <param name="loadSheddingLimit">Percentage of CPU which triggers queue read rate reduction</param>
        /// <param name="options">The silo statistics options.</param>
        /// <param name="environmentStatisticsProvider">The silo environment statistics.</param>
        /// <returns>The flow controller.</returns>
        public static IQueueFlowController CreateAsPercentageOfCPU(int loadSheddingLimit, LoadSheddingOptions options, IEnvironmentStatisticsProvider environmentStatisticsProvider)
        {
            if (loadSheddingLimit < 0 || loadSheddingLimit > 100) throw new ArgumentOutOfRangeException(nameof(loadSheddingLimit), "Value must be between 0-100");
            return new LoadShedQueueFlowController(loadSheddingLimit, options, environmentStatisticsProvider);
        }

        private LoadShedQueueFlowController(int loadSheddingLimit, LoadSheddingOptions options, IEnvironmentStatisticsProvider environmentStatisticsProvider)
        {
            this.options = options;
            if (loadSheddingLimit < 0 || loadSheddingLimit > 100) throw new ArgumentOutOfRangeException(nameof(loadSheddingLimit), "Value must be between 0-100");
            this.loadSheddingLimit = loadSheddingLimit != 0 ? loadSheddingLimit : int.MaxValue;
            this.environmentStatisticsProvider = environmentStatisticsProvider;
        }

        /// <inheritdoc/>
        public int GetMaxAddCount()
        {
            return options.LoadSheddingEnabled && environmentStatisticsProvider.GetEnvironmentStatistics().FilteredCpuUsagePercentage > loadSheddingLimit ? 0 : int.MaxValue;
        }
    }
}

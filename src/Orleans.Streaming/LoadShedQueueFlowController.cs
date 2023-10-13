
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
        private readonly IHostEnvironmentStatistics _hostEnvironmentStatistics;

        /// <summary>
        /// Creates a flow controller triggered when the CPU reaches a percentage of the cluster load shedding limit.
        /// This is intended to reduce queue read rate prior to causing the silo to shed load.
        /// Note:  Triggered only when load shedding is enabled.
        /// </summary>
        /// <param name="options">The silo statistics options.</param>
        /// <param name="percentOfSiloSheddingLimit">Percentage of load shed limit which triggers a reduction of queue read rate.</param>
        /// <param name="hostEnvironmentStatistics">The host environment statistics.</param>
        /// <returns>The flow controller.</returns>
        public static IQueueFlowController CreateAsPercentOfLoadSheddingLimit(LoadSheddingOptions options, IHostEnvironmentStatistics hostEnvironmentStatistics, int percentOfSiloSheddingLimit = LoadSheddingOptions.DefaultLoadSheddingLimit)
        {
            if (percentOfSiloSheddingLimit < 0.0 || percentOfSiloSheddingLimit > 100.0) throw new ArgumentOutOfRangeException(nameof(percentOfSiloSheddingLimit), "Percent value must be between 0-100");
            // Start shedding before silo reaches shedding limit.
            return new LoadShedQueueFlowController((int)(options.LoadSheddingLimit * (percentOfSiloSheddingLimit / 100.0)), options, hostEnvironmentStatistics);
        }

        /// <summary>
        /// Creates a flow controller triggered when the CPU reaches the specified limit.
        /// Note:  Triggered only when load shedding is enabled.
        /// </summary>
        /// <param name="loadSheddingLimit">Percentage of CPU which triggers queue read rate reduction</param>
        /// <param name="options">The silo statistics options.</param>
        /// <param name="hostEnvironmentStatistics">The host environment statistics.</param>
        /// <returns>The flow controller.</returns>
        public static IQueueFlowController CreateAsPercentageOfCPU(int loadSheddingLimit, LoadSheddingOptions options, IHostEnvironmentStatistics hostEnvironmentStatistics)
        {
            if (loadSheddingLimit < 0 || loadSheddingLimit > 100) throw new ArgumentOutOfRangeException(nameof(loadSheddingLimit), "Value must be between 0-100");
            return new LoadShedQueueFlowController(loadSheddingLimit, options, hostEnvironmentStatistics);
        }

        private LoadShedQueueFlowController(int loadSheddingLimit, LoadSheddingOptions options, IHostEnvironmentStatistics hostEnvironmentStatistics)
        {
            this.options = options;
            if (loadSheddingLimit < 0 || loadSheddingLimit > 100) throw new ArgumentOutOfRangeException(nameof(loadSheddingLimit), "Value must be between 0-100");
            this.loadSheddingLimit = loadSheddingLimit != 0 ? loadSheddingLimit : int.MaxValue;
            _hostEnvironmentStatistics = hostEnvironmentStatistics;
        }

        /// <inheritdoc/>
        public int GetMaxAddCount()
        {
            return options.LoadSheddingEnabled && GetCpuUsage() > loadSheddingLimit ? 0 : int.MaxValue;
        }

        private float GetCpuUsage() => _hostEnvironmentStatistics.CpuUsage ?? default;
    }
}

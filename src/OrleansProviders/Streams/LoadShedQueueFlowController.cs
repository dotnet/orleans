
using System;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Flow control triggered by silo load shedding.
    /// All or nothing trigger.  Will request maxint, or 0.
    /// </summary>
    public class LoadShedQueueFlowController : IQueueFlowController
    {
        private readonly LoadSheddingOptions options;
        private readonly double loadSheddingLimit;
        private FloatValueStatistic cpuStatistic;

        /// <summary>
        /// Creates a flow controller triggered when the CPU reaches a percentage of the cluster load shedding limit.
        /// This is intended to reduce queue read rate prior to causing the silo to shed load.
        /// Note:  Triggered only when load shedding is enabled.
        /// </summary>
        /// <param name="options">The silo satistics options.</param>
        /// <param name="percentOfSiloSheddingLimit">Percentage of load shed limit which triggers a reduction of queue read rate.</param>
        /// <returns></returns>
        public static IQueueFlowController CreateAsPercentOfLoadSheddingLimit(LoadSheddingOptions options, int percentOfSiloSheddingLimit = LoadSheddingOptions.DEFAULT_LOAD_SHEDDING_LIMIT)
        {
            if (percentOfSiloSheddingLimit < 0.0 || percentOfSiloSheddingLimit > 100.0) throw new ArgumentOutOfRangeException(nameof(percentOfSiloSheddingLimit), "Percent value must be between 0-100");
            // Start shedding before silo reaches shedding limit.
            return new LoadShedQueueFlowController((int)(options.LoadSheddingLimit * (percentOfSiloSheddingLimit / 100.0)), options);
        }

        /// <summary>
        /// Creates a flow controller triggered when the CPU reaches the specified limit.
        /// Note:  Triggered only when load shedding is enabled.
        /// </summary>
        /// <param name="loadSheddingLimit">Percentage of CPU which triggers queue read rate reduction</param>
        /// <param name="options">The silo satistics options.</param>
        /// <returns></returns>
        public static IQueueFlowController CreateAsPercentageOfCPU(int loadSheddingLimit, LoadSheddingOptions options)
        {
            if (loadSheddingLimit < 0 || loadSheddingLimit > 100) throw new ArgumentOutOfRangeException(nameof(loadSheddingLimit), "Value must be between 0-100");
            return new LoadShedQueueFlowController(loadSheddingLimit, options);
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="loadSheddingLimit"></param>
        /// <param name="getNodeConfig">The method used to get the current node configuration.</param>
        private LoadShedQueueFlowController(int loadSheddingLimit, LoadSheddingOptions options)
        {
            this.options = options;
            if (loadSheddingLimit < 0 || loadSheddingLimit > 100) throw new ArgumentOutOfRangeException(nameof(loadSheddingLimit), "Value must be between 0-100");
            this.loadSheddingLimit = loadSheddingLimit != 0 ? loadSheddingLimit : int.MaxValue;
        }

        /// <summary>
        /// The limit of the maximum number of items that can be added
        /// </summary>
        public int GetMaxAddCount()
        {
            return options.LoadSheddingEnabled && GetCpuUsage() > loadSheddingLimit ? 0 : int.MaxValue;
        }

        private float GetCpuUsage()
        {
            if (cpuStatistic == null)
            {
                cpuStatistic = FloatValueStatistic.Find(StatisticNames.RUNTIME_CPUUSAGE);
            }
            return cpuStatistic?.GetCurrentValue() ?? default(float);
        }
    }
}

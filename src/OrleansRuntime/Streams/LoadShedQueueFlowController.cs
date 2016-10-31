
using System;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Flow control triggered by silo load shedding.
    /// All or nothing trigger.  Will request maxint, or 0.
    /// </summary>
    public class LoadShedQueueFlowController : IQueueFlowController
    {
        private readonly double loadSheddingLimit;
        private FloatValueStatistic cpuStatistic;

        /// <summary>
        /// Constructor.
        /// Load setting is based off of cluster load shedding limit
        /// Defaults to 95% of silo load shedding.  We don't want queue readers to swamp grain message processing.
        /// </summary>
        public LoadShedQueueFlowController(double percentOfSiloSheddingLimit = 95.0)
        {
            if(percentOfSiloSheddingLimit < 0.0 || percentOfSiloSheddingLimit > 100.0) throw new ArgumentOutOfRangeException(nameof(percentOfSiloSheddingLimit), "Percent value must be between 0-100");
            // Start shedding before silo reaches shedding limit.
            loadSheddingLimit = Silo.CurrentSilo.LocalConfig.LoadSheddingLimit*(percentOfSiloSheddingLimit/100);
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="loadSheddingLimit"></param>
        public LoadShedQueueFlowController(int loadSheddingLimit)
        {
            if (loadSheddingLimit < 0 || loadSheddingLimit > 100) throw new ArgumentOutOfRangeException(nameof(loadSheddingLimit), "Value must be between 0-100");
            this.loadSheddingLimit = loadSheddingLimit != 0 ? loadSheddingLimit : int.MaxValue;
        }

        /// <summary>
        /// The limit of the maximum number of items that can be added
        /// </summary>
        public int GetMaxAddCount()
        {
            return Silo.CurrentSilo.LocalConfig.LoadSheddingEnabled && GetCpuUsage() > loadSheddingLimit ? 0 : int.MaxValue;
        }

        private float GetCpuUsage()
        {
            if (this.cpuStatistic == null)
            {
                this.cpuStatistic = FloatValueStatistic.Find(StatisticNames.RUNTIME_CPUUSAGE);
            }
            return cpuStatistic?.GetCurrentValue() ?? default(float);
        }
    }
}

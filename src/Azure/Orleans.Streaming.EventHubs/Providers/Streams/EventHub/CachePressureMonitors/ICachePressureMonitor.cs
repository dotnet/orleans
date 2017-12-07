using Orleans.Providers.Streams.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Cache pressure monitor records pressure contribution to the cache, and determine if the cache is under pressure based on its 
    /// back pressure algorithm
    /// </summary>
    public interface ICachePressureMonitor
    {
        /// <summary>
        /// Record cache pressure contribution to the monitor
        /// </summary>
        /// <param name="cachePressureContribution"></param>
        void RecordCachePressureContribution(double cachePressureContribution);

        /// <summary>
        /// Determine if the monitor is under pressure
        /// </summary>
        /// <param name="utcNow"></param>
        /// <returns></returns>
        bool IsUnderPressure(DateTime utcNow);

        /// <summary>
        /// Cache monitor which is used to report cache related metrics
        /// </summary>
        ICacheMonitor CacheMonitor { set; }
    }
}

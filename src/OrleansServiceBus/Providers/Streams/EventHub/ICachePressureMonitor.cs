using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrleansServiceBus.Providers.Streams.EventHub
{
    public interface ICachePressureMonitor
    {
        void RecordCachePressureContribution(double cachePressureContribution);

        bool IsUnderPressure(DateTime utcNow);
    }

    internal class AggregatedCachePressureMonitor : List<ICachePressureMonitor>, ICachePressureMonitor
    {
        public void RecordCachePressureContribution(double cachePressureContribution)
        {
            this.ForEach(monitor =>
            {
                monitor.RecordCachePressureContribution(cachePressureContribution);
            });
        }

        public void AddCachePressureMonitor(ICachePressureMonitor monitor)
        {
            this.Add(monitor);
        }

        public bool IsUnderPressure(DateTime utcNow)
        {
            bool isUnderPressure = false;
            //if any mornitor in this monitor list is under pressure, then return true
            this.ForEach(monitor =>
            {
                if (monitor.IsUnderPressure(utcNow))
                {
                    isUnderPressure = true;
                }
            });
            return isUnderPressure;
        }
    }
}

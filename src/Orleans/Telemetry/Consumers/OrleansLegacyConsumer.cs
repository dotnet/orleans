using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Telemetry.Consumers
{
    public class OrleansLegacyConsumer : IMetricTelemetryConsumer
    { 
        public void Flush()
        {
           
        }

        public void Close()
        {
            
        }

        public void TrackMetric(string name, double value, IDictionary<string, string> properties = null)
        {
            CounterStatistic.FindOrCreate(new StatisticName(name)).IncrementBy((long)value);
        }

        public void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null)
        {
            CounterStatistic.FindOrCreate(new StatisticName(name)).IncrementBy(value.Ticks);
        }

        public void IncrementMetric(string name)
        {
            CounterStatistic.FindOrCreate(new StatisticName(name)).Increment();
        }

        public void IncrementMetric(string name, double value)
        {
            CounterStatistic.FindOrCreate(new StatisticName(name)).IncrementBy((long)value);
        }

        public void DecrementMetric(string name)
        {
            CounterStatistic.FindOrCreate(new StatisticName(name)).DecrementBy(1);
        }

        public void DecrementMetric(string name, double value)
        {
            CounterStatistic.FindOrCreate(new StatisticName(name)).DecrementBy((long)value); 
        }
    }
}

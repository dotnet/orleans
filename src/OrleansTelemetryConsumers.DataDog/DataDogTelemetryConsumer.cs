using Newtonsoft.Json;
using Orleans.Runtime;
using StatsdClient;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.TelemetryConsumers.DataDog
{
    public class DataDogTelemetryConsumer : IMetricTelemetryConsumer, IEventTelemetryConsumer
    {
        public DataDogTelemetryConsumer(string statsdServerName = "127.0.0.1", int statsdPort = 8125, string prefix = "")
        {
            var dogstatsdConfig = new StatsdConfig
            {
                StatsdServerName = statsdServerName,
                StatsdPort = statsdPort,
                Prefix = prefix
            };

            DogStatsd.Configure(dogstatsdConfig);
        }

        public void DecrementMetric(string name)
        {
            DogStatsd.Decrement(name);
        }

        public void DecrementMetric(string name, double value)
        {
            DogStatsd.Decrement(name, (int)value);
        }

        public void IncrementMetric(string name)
        {
            DogStatsd.Increment(name);
        }

        public void IncrementMetric(string name, double value)
        {
            DogStatsd.Increment(name, (int)value);
        }

        public void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null)
        {
            DogStatsd.Increment(name, (int)value.TotalMilliseconds, tags: ToStringArray(properties));
        }

        public void TrackMetric(string name, double value, IDictionary<string, string> properties = null)
        {
            DogStatsd.Increment(name, (int)value, tags: ToStringArray(properties));
        }

        private static string[] ToStringArray<K, V>(IDictionary<K, V> dic)
        {
            if (dic != null)
            {
                return dic.Select(p => JsonConvert.SerializeObject(p)).ToArray();
            }
            return null;
        }

        public void TrackEvent(string eventName, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
            DogStatsd.Event(eventName, string.Join(",", ToStringArray(properties)), tags: ToStringArray(properties));
        }
    }
}

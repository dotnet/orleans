using Orleans.Runtime;
using System;
using System.Collections.Generic;

namespace Orleans.Configuration.Options
{
    public class TelemetryOptions
    {
        internal IList<Type> Consumers { get; set; } = new List<Type>();
    }

    public static class TelemetryOptionsExtensions
    {
        public static TelemetryOptions AddConsumer<T>(this TelemetryOptions options) where T : ITelemetryConsumer
        {
            options.Consumers.Add(typeof(T));
            return options;
        }
    }
}

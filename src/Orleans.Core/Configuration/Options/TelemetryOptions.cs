using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Telemetry consumer settings
    /// </summary>
    public class TelemetryOptions
    {
        /// <summary>
        /// Configured telemetry consumers
        /// </summary>
        public IList<Type> Consumers { get; set; } = new List<Type>();
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

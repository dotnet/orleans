using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Telemetry consumer settings
    /// </summary>
    public class TelemetryOptions
    {
        /// <summary>
        /// Gets or sets the list of telemetry consumer types.
        /// </summary>
        /// <seealso cref="TelemetryOptionsExtensions.AddConsumer{T}"/>.
        public IList<Type> Consumers { get; set; } = new List<Type>();
    }

    /// <summary>
    /// Extensions for <see cref="TelemetryOptions"/>.
    /// </summary>
    public static class TelemetryOptionsExtensions
    {
        /// <summary>
        /// Adds a telemetry consumer to <see cref="TelemetryOptions.Consumers"/>.
        /// </summary>
        /// <typeparam name="T">The telemetry consumer type.</typeparam>
        /// <param name="options">The options.</param>
        /// <returns>The options.</returns>
        public static TelemetryOptions AddConsumer<T>(this TelemetryOptions options) where T : ITelemetryConsumer
        {
            options.Consumers.Add(typeof(T));
            return options;
        }
    }
}

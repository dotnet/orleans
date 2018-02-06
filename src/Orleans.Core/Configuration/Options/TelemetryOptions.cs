using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Hosting
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

    public class TelemetryOptionsFormatter : IOptionFormatter<TelemetryOptions>
    {
        public string Category { get; }

        public string Name => nameof(TelemetryOptions);

        private TelemetryOptions options;
        public TelemetryOptionsFormatter(IOptions<TelemetryOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            return new List<string>()
                {OptionFormattingUtilities.Format(nameof(this.options.Consumers), string.Join(";", this.options.Consumers))};
        }
    }
}

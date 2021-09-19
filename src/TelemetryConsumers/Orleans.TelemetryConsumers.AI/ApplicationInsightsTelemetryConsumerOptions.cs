using Microsoft.ApplicationInsights.Extensibility;

namespace Orleans.Configuration
{
    public class ApplicationInsightsTelemetryConsumerOptions
    {
        /// <summary>
        /// Instrumentation Key of the Application Insights instance.
        /// Will be ignored if <see cref="TelemetryClient" /> is provided.
        /// </summary>
        [Redact]
        public string InstrumentationKey { get; set; }

        /// <summary>
        /// The <see cref="TelemetryConfiguration" /> instance.
        /// </summary>
        public TelemetryConfiguration TelemetryConfiguration  { get; set; }
    }
}

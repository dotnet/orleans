
namespace Orleans.Configuration
{
    public class ApplicationInsightsTelemetryConsumerOptions
    {
        [Redact]
        public string InstrumentationKey { get; set; }
    }
}

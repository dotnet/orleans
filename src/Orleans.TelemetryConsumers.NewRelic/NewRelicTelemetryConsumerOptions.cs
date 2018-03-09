
namespace Orleans.Configuration
{
    public class NewRelicTelemetryConsumerOptions
    {
        [Redact]
        public string InstrumentationKey { get; set; }
    }
}

namespace Orleans.Runtime
{
    /// <summary>
    /// Marker interface for all Telemetry Consumers
    /// </summary>
    public interface ITelemetryConsumer
    {
        void Flush();
        void Close();
    }
}

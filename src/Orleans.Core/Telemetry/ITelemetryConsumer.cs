namespace Orleans.Runtime
{
    /// <summary>
    /// Marker interface for all Telemetry Consumers
    /// </summary>
    public interface ITelemetryConsumer
    {
        /// <summary>
        /// Flushes this instance.
        /// </summary>
        void Flush();

        /// <summary>
        /// Closes this instance.
        /// </summary>
        void Close();
    }
}

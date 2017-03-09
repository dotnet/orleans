namespace Orleans.Runtime
{
    /// <summary>
    /// An interface used to consume log entries, when a Close function is also supported. 
    /// Instances of a class implementing this should be added to <see cref="LogManager.LogConsumers"/> collection in order to retrieve events.
    /// </summary>
    public interface ICloseableLogConsumer : ILogConsumer
    {
        /// <summary>Close this log.</summary>
        void Close();
    }
}
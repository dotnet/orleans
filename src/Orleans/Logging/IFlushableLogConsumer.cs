namespace Orleans.Runtime
{
    /// <summary>
    /// An interface used to consume log entries, when a Flush function is also supported. 
    /// Instances of a class implementing this should be added to <see cref="LogManager.LogConsumers"/> collection in order to retrieve events.
    /// </summary>
    public interface IFlushableLogConsumer : ILogConsumer
    {
        /// <summary>Flush any pending log writes.</summary>
        void Flush();
    }
}
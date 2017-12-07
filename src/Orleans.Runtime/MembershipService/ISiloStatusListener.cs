namespace Orleans.Runtime
{
    /// <summary>
    /// Interface for types which listen to silo status change notifications.
    /// </summary>
    /// <remarks>
    /// To be implemented by different in-silo runtime components that are interested in silo status notifications from ISiloStatusOracle.
    /// </remarks>
    public interface ISiloStatusListener
    {
        /// <summary>
        /// Receive notifications about silo status events. 
        /// </summary>
        /// <param name="updatedSilo">A silo to update about.</param>
        /// <param name="status">The status of a silo.</param>
        void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status);
    }
}

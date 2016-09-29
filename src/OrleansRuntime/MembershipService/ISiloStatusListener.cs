namespace Orleans.Runtime
{
    // Interface to receive notifications from ISiloStatusOracle about status updates of different silos.
    // To be implemented by different in-silo runtime components that are interested in silo status notifications from ISiloStatusOracle.
    internal interface ISiloStatusListener
    {
        /// <summary>
        /// Receive notifications about silo status events. 
        /// </summary>
        /// <param name="updatedSilo">A silo to update about.</param>
        /// <param name="status">The status of a silo.</param>
        /// <returns></returns>
        void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status);
    }
}

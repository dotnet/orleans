namespace Orleans.Runtime.ConsistentRing
{
    // someday, this will be the only provider for the ring, i.e., directory service will use this

    internal interface IConsistentRingProvider
    {
        /// <summary>
        /// Get the responsbility range of the current silo
        /// </summary>
        /// <returns></returns>
        IRingRange GetMyRange();

        // the following two are similar to the ISiloStatusOracle interface ... this replaces the 'OnRangeChanged' because OnRangeChanged only supports one subscription

        /// <summary>
        /// Subscribe to receive range change notifications
        /// </summary>
        /// <param name="observer">An observer interface to receive range change notifications.</param>
        /// <returns>bool value indicating that subscription succeeded or not.</returns>
        bool SubscribeToRangeChangeEvents(IRingRangeListener observer);

        /// <summary>
        /// Unsubscribe from receiving range change notifications
        /// </summary>
        /// <param name="observer">An observer interface to receive range change notifications.</param>
        /// <returns>bool value indicating that unsubscription succeeded or not</returns>
        bool UnSubscribeFromRangeChangeEvents(IRingRangeListener observer);

        /// <summary>
        /// Get the silo responsible for <paramref name="key"/> according to consistent hashing
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        SiloAddress GetPrimaryTargetSilo(uint key);
    }

    // similar to ISiloStatusListener
    internal interface IRingRangeListener
    {
        void RangeChangeNotification(IRingRange old, IRingRange now, bool increased);
    }
}

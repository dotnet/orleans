using System.Threading.Tasks;


namespace Orleans.Runtime
{
    internal interface IConsistentRingProviderForGrains
    {
        /// <summary>
        /// Get the responsibility range of the current silo
        /// </summary>
        /// <returns></returns>
        IRingRange GetMyRange();

        /// <summary>
        /// Subscribe to receive range change notifications
        /// </summary>
        /// <param name="observer">An observer interface to receive range change notifications.</param>
        /// <returns>bool value indicating that subscription succeeded or not.</returns>
        bool SubscribeToRangeChangeEvents(IAsyncRingRangeListener observer);

        /// <summary>
        /// Unsubscribe from receiving range change notifications
        /// </summary>
        /// <param name="observer">An observer interface to receive range change notifications.</param>
        /// <returns>bool value indicating that unsubscription succeeded or not</returns>
        bool UnSubscribeFromRangeChangeEvents(IAsyncRingRangeListener observer);
    }

    // This has to be a separate interface, not polymorphic with IRingRangeListener,
    // since IRingRangeListener is implemented by SystemTarget and thus if it becomes grain interface 
    // it would need to be system target interface (with SiloAddress as first argument).
    internal interface IAsyncRingRangeListener
    {
        Task RangeChangeNotification(IRingRange old, IRingRange now);
    }
}

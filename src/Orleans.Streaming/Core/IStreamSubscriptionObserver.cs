using System.Threading.Tasks;

namespace Orleans.Streams.Core
{
    /// <summary>
    /// When implemented by a grain, notifies the grain of any new or resuming subscriptions.
    /// </summary>
    public interface IStreamSubscriptionObserver
    {
        /// <summary>
        /// Called when this grain receives a message for a stream which it has not yet explicitly subscribed to or resumed.
        /// </summary>
        /// <param name="handleFactory">The handle factory.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory);
    }
}

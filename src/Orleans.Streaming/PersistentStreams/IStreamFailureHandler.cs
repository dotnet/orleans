using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Functionality for handling stream failures.
    /// </summary>
    public interface IStreamFailureHandler
    {
        /// <summary>
        /// Gets a value indicating whether the subscription should fault when there is an error.
        /// </summary>
        /// <value><see langword="true" /> if the subscription should fault when there is an error; otherwise, <see langword="false" />.</value>
        bool ShouldFaultSubsriptionOnError { get; }

        /// <summary>
        /// Called once all measures to deliver an event to a consumer have been exhausted.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="streamProviderName">Name of the stream provider.</param>
        /// <param name="streamIdentity">The stream identity.</param>
        /// <param name="sequenceToken">The sequence token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task OnDeliveryFailure(GuidId subscriptionId, string streamProviderName, StreamId streamIdentity, StreamSequenceToken sequenceToken);

        /// <summary>
        /// Should be called when establishing a subscription failed.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="streamProviderName">Name of the stream provider.</param>
        /// <param name="streamIdentity">The stream identity.</param>
        /// <param name="sequenceToken">The sequence token.</param>
        /// <returns>A <see cref="Task" /> representing the operation.</returns>
        Task OnSubscriptionFailure(GuidId subscriptionId, string streamProviderName, StreamId streamIdentity, StreamSequenceToken sequenceToken);
    }
}

using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    public interface IStreamFailureHandler
    {
        bool ShouldFaultSubsriptionOnError { get; }

        /// <summary>
        /// Should be called once all measures to deliver an event to a consumer have been exhausted.
        /// </summary>
        /// <param name="subscriptionId"></param>
        /// <param name="streamProviderName"></param>
        /// <param name="streamIdentity"></param>
        /// <param name="sequenceToken"></param>
        /// <returns></returns>
        Task OnDeliveryFailure(GuidId subscriptionId, string streamProviderName, StreamId streamIdentity, StreamSequenceToken sequenceToken);


        /// <summary>
        /// Should be called when establishing a subscription failed.
        /// </summary>
        /// <param name="subscriptionId"></param>
        /// <param name="streamProviderName"></param>
        /// <param name="streamIdentity"></param>
        /// <param name="sequenceToken"></param>
        /// <returns></returns>
        Task OnSubscriptionFailure(GuidId subscriptionId, string streamProviderName, StreamId streamIdentity, StreamSequenceToken sequenceToken);
    }
}

using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    public class NoOpStreamDeliveryFailureHandler : IStreamFailureHandler
    {
        public NoOpStreamDeliveryFailureHandler()
            : this(false)
        {
        }

        public NoOpStreamDeliveryFailureHandler(bool faultOnError)
        {
            ShouldFaultSubsriptionOnError = faultOnError;
        }

        public bool ShouldFaultSubsriptionOnError { get; }

        /// <summary>
        /// Should be called when an event could not be delivered to a consumer, after exhausting retry attempts.
        /// </summary>
        public Task OnDeliveryFailure(GuidId subscriptionId, string streamProviderName, StreamId streamId,
            StreamSequenceToken sequenceToken)
        {
            return Task.CompletedTask;
        }

        public Task OnSubscriptionFailure(GuidId subscriptionId, string streamProviderName, StreamId streamId,
            StreamSequenceToken sequenceToken)
        {
            return Task.CompletedTask;
        }
    }
}

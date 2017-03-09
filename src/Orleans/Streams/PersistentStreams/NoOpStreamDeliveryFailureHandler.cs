using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    public class NoOpStreamDeliveryFailureHandler : IStreamFailureHandler
    {
        public NoOpStreamDeliveryFailureHandler()
            : this(true)
        {
        }

        public NoOpStreamDeliveryFailureHandler(bool faultOnError)
        {
            ShouldFaultSubsriptionOnError = faultOnError;
        }

        public bool ShouldFaultSubsriptionOnError { get; private set; }

        /// <summary>
        /// Should be called when an event could not be delivered to a consumer, after exhausting retry attempts.
        /// </summary>
        public Task OnDeliveryFailure(GuidId subscriptionId, string streamProviderName, IStreamIdentity streamIdentity,
            StreamSequenceToken sequenceToken)
        {
            return TaskDone.Done;
        }

        public Task OnSubscriptionFailure(GuidId subscriptionId, string streamProviderName, IStreamIdentity streamIdentity,
            StreamSequenceToken sequenceToken)
        {
            return TaskDone.Done;
        }
    }
}

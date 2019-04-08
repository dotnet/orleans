using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    public class NoOpStreamDeliveryFailureHandler : IStreamFailureHandler
    {
        private NoOpStreamDeliveryFailureHandler()
            : this(false)
        {
        }

        private NoOpStreamDeliveryFailureHandler(bool faultOnError)
        {
            ShouldFaultSubscriptionOnError = faultOnError;
        }

        public bool ShouldFaultSubscriptionOnError { get; }

        /// <summary>
        /// Should be called when an event could not be delivered to a consumer, after exhausting retry attempts.
        /// </summary>
        public Task OnDeliveryFailure(GuidId subscriptionId, string streamProviderName, IStreamIdentity streamIdentity,
            StreamSequenceToken sequenceToken)
        {
            return Task.CompletedTask;
        }

        public Task OnSubscriptionFailure(GuidId subscriptionId, string streamProviderName, IStreamIdentity streamIdentity,
            StreamSequenceToken sequenceToken)
        {
            return Task.CompletedTask;
        }

        public static IStreamFailureHandler Create(IServiceProvider service)
        {
            return new NoOpStreamDeliveryFailureHandler();
        }

        public static IStreamFailureHandler Create(IServiceProvider service, string name)
        {
            return Create(service);
        }
    }
}

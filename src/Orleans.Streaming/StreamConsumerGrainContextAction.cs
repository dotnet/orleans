using Orleans.Runtime;
using Orleans.Streams.Core;

namespace Orleans.Streams
{
    internal class StreamConsumerGrainContextAction : IConfigureGrainContext
    {
        private readonly IStreamProviderRuntime _streamProviderRuntime;

        public StreamConsumerGrainContextAction(
            IStreamProviderRuntime streamProviderRuntime)
        {
            _streamProviderRuntime = streamProviderRuntime;
        }

        public void Configure(IGrainContext context)
        {
            if (context.GrainInstance is IStreamSubscriptionObserver observer)
            {
                InstallStreamConsumerExtension(context, observer as IStreamSubscriptionObserver);
            }
        }

        private void InstallStreamConsumerExtension(IGrainContext context, IStreamSubscriptionObserver observer)
        {
            _streamProviderRuntime.BindExtension<StreamConsumerExtension, IStreamConsumerExtension>(() => new StreamConsumerExtension(_streamProviderRuntime, observer));
        }
    }
}

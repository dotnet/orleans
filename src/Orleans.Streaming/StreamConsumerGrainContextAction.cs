using Orleans.Runtime;
using Orleans.Streams.Core;

namespace Orleans.Streams
{
    /// <summary>
    /// Installs an <see cref="IStreamConsumerExtension"/> extension on a <see cref="IGrainContext"/> for grains which implement <see cref="IStreamSubscriptionObserver"/>.
    /// </summary>
    internal class StreamConsumerGrainContextAction : IConfigureGrainContext
    {
        private readonly IStreamProviderRuntime _streamProviderRuntime;

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamConsumerGrainContextAction"/> class.
        /// </summary>
        /// <param name="streamProviderRuntime">The stream provider runtime.</param>
        public StreamConsumerGrainContextAction(IStreamProviderRuntime streamProviderRuntime)
        {
            _streamProviderRuntime = streamProviderRuntime;
        }

        /// <inheritdoc/>
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

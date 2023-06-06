using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Streams;
using Microsoft.Extensions.Logging;

namespace Orleans.Providers
{
    internal class ClientStreamingProviderRuntime : IStreamProviderRuntime, ILifecycleParticipant<IClusterClientLifecycle>
    {
        private readonly IStreamPubSub grainBasedPubSub;
        private readonly IStreamPubSub implicitPubSub;
        private readonly IStreamPubSub combinedGrainBasedAndImplicitPubSub;
        private StreamDirectory streamDirectory;
        private readonly IInternalGrainFactory grainFactory;
        private readonly ImplicitStreamSubscriberTable implicitSubscriberTable;
        private readonly ClientGrainContext clientContext;
        private readonly IRuntimeClient runtimeClient;
        private readonly ILogger timerLogger;

        public ClientStreamingProviderRuntime(
            IInternalGrainFactory grainFactory,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory,
            ImplicitStreamSubscriberTable implicitSubscriberTable,
            ClientGrainContext clientContext)
        {
            this.grainFactory = grainFactory;
            ServiceProvider = serviceProvider;
            this.implicitSubscriberTable = implicitSubscriberTable;
            this.clientContext = clientContext;
            runtimeClient = serviceProvider.GetService<IRuntimeClient>();
            //all async timer created through current class all share this logger for perf reasons
            timerLogger = loggerFactory.CreateLogger<AsyncTaskSafeTimer>();
            grainBasedPubSub = new GrainBasedPubSubRuntime(GrainFactory);
            var tmp = new ImplicitStreamPubSub(this.grainFactory, this.implicitSubscriberTable);
            implicitPubSub = tmp;
            combinedGrainBasedAndImplicitPubSub = new StreamPubSubImpl(grainBasedPubSub, tmp);
            streamDirectory = new StreamDirectory();
        }

        public IGrainFactory GrainFactory => grainFactory;
        public IServiceProvider ServiceProvider { get; }

        public StreamDirectory GetStreamDirectory() => streamDirectory;

        public async Task Reset(bool cleanup = true)
        {
            if (streamDirectory != null)
            {
                var tmp = streamDirectory;
                streamDirectory = null; // null streamDirectory now, just to make sure we call cleanup only once, in all cases.
                if (cleanup)
                {
                    await tmp.Cleanup(true, true);
                }
            }
        }

        public string ExecutingEntityIdentity() => runtimeClient.CurrentActivationIdentity;

        public (TExtension, TExtensionInterface) BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : class, TExtensionInterface
            where TExtensionInterface : class, IGrainExtension => clientContext.GetOrSetExtension<TExtension, TExtensionInterface>(newExtensionFunc);

        public IStreamPubSub PubSub(StreamPubSubType pubSubType)
        {
            switch (pubSubType)
            {
                case StreamPubSubType.ExplicitGrainBasedAndImplicit:
                    return combinedGrainBasedAndImplicitPubSub;
                case StreamPubSubType.ExplicitGrainBasedOnly:
                    return grainBasedPubSub;
                case StreamPubSubType.ImplicitOnly:
                    return implicitPubSub;
                default:
                    return null;
            }
        }

        public void Participate(IClusterClientLifecycle lifecycle)
        {
            lifecycle.Subscribe<ClientStreamingProviderRuntime>(ServiceLifecycleStage.RuntimeInitialize,
                ct => Task.CompletedTask,
                async ct => await Reset(!ct.IsCancellationRequested));
        }
    }
}

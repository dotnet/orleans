using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Streams;
using Microsoft.Extensions.Logging;

namespace Orleans.Providers
{
    internal class ClientProviderRuntime : IStreamProviderRuntime
    {
        private IStreamPubSub grainBasedPubSub;
        private IStreamPubSub implicitPubSub;
        private IStreamPubSub combinedGrainBasedAndImplicitPubSub;
        private StreamDirectory streamDirectory;
        private readonly IInternalGrainFactory grainFactory;
        private readonly ImplicitStreamSubscriberTable implicitSubscriberTable;
        private readonly ClientGrainContext clientContext;
        private readonly IRuntimeClient runtimeClient;
        private readonly ILogger timerLogger;

        public ClientProviderRuntime(
            IInternalGrainFactory grainFactory,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory,
            ImplicitStreamSubscriberTable implicitSubscriberTable,
            ClientGrainContext clientContext)
        {
            this.grainFactory = grainFactory;
            this.ServiceProvider = serviceProvider;
            this.implicitSubscriberTable = implicitSubscriberTable;
            this.clientContext = clientContext;
            this.runtimeClient = serviceProvider.GetService<IRuntimeClient>();
            //all async timer created through current class all share this logger for perf reasons
            this.timerLogger = loggerFactory.CreateLogger<AsyncTaskSafeTimer>();
        }

        public IGrainFactory GrainFactory => this.grainFactory;
        public IServiceProvider ServiceProvider { get; }

        public void StreamingInitialize() 
        {
            grainBasedPubSub = new GrainBasedPubSubRuntime(GrainFactory);
            var tmp = new ImplicitStreamPubSub(this.grainFactory, this.implicitSubscriberTable);
            implicitPubSub = tmp;
            combinedGrainBasedAndImplicitPubSub = new StreamPubSubImpl(grainBasedPubSub, tmp);
            streamDirectory = new StreamDirectory();
        }

        public StreamDirectory GetStreamDirectory()
        {
            return streamDirectory;
        }

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

        public string ExecutingEntityIdentity()
        {
            return this.runtimeClient.CurrentActivationIdentity;
        }

        public void RegisterSystemTarget(ISystemTarget target)
        {
            throw new NotImplementedException();
        }

        public void UnregisterSystemTarget(ISystemTarget target)
        {
            throw new NotImplementedException();
        }

        public IDisposable RegisterTimer(Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period)
        {
            return new AsyncTaskSafeTimer(this.timerLogger, asyncCallback, state, dueTime, period);
        }

        public (TExtension, TExtensionInterface) BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : TExtensionInterface
            where TExtensionInterface : IGrainExtension
        {
            return this.clientContext.GetOrSetExtension<TExtension, TExtensionInterface>(newExtensionFunc);
        }

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

        public IConsistentRingProviderForGrains GetConsistentRingProvider(int mySubRangeIndex, int numSubRanges)
        {
            throw new NotImplementedException("GetConsistentRingProvider");
        }
    }
}

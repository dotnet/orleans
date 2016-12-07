using System;
using System.Reflection;
using System.Threading.Tasks;

using Orleans.CodeGeneration;
using Orleans.Concurrency;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ConsistentRing;
using Orleans.Streams;

namespace Orleans.Runtime.Providers
{
    internal class SiloProviderRuntime : ISiloSideStreamProviderRuntime
    {
        private readonly Silo silo;
        private readonly IConsistentRingProvider consistentRingProvider;
        private readonly ISiloRuntimeClient runtimeClient;
        private readonly IStreamPubSub grainBasedPubSub;
        private readonly IStreamPubSub implictPubSub;
        private readonly IStreamPubSub combinedGrainBasedAndImplicitPubSub;
        
        private InvokeInterceptor invokeInterceptor;

        public IGrainFactory GrainFactory { get; }
        public IServiceProvider ServiceProvider { get; }

        public Guid ServiceId { get; }
        public string SiloIdentity { get; }

        public SiloProviderRuntime(
            Silo silo,
            GlobalConfiguration config,
            IGrainFactory grainFactory,
            IConsistentRingProvider consistentRingProvider,
            ISiloRuntimeClient runtimeClient,
            IServiceProvider serviceProvider,
            ImplicitStreamSubscriberTable implicitStreamSubscriberTable)
        {
            this.silo = silo;
            this.consistentRingProvider = consistentRingProvider;
            this.runtimeClient = runtimeClient;
            this.ServiceId = config.ServiceId;
            this.SiloIdentity = silo.SiloAddress.ToLongString();
            this.GrainFactory = grainFactory;
            this.ServiceProvider = serviceProvider;

            this.grainBasedPubSub = new GrainBasedPubSubRuntime(this.GrainFactory);
            var tmp = new ImplicitStreamPubSub(implicitStreamSubscriberTable);
            this.implictPubSub = tmp;
            this.combinedGrainBasedAndImplicitPubSub = new StreamPubSubImpl(this.grainBasedPubSub, tmp);
        }

        public void SetInvokeInterceptor(InvokeInterceptor interceptor)
        {
            this.invokeInterceptor = interceptor;
        }

        public InvokeInterceptor GetInvokeInterceptor()
        {
            return this.invokeInterceptor;
        }

        public Logger GetLogger(string loggerName)
        {
            return LogManager.GetLogger(loggerName, LoggerType.Provider);
        }

        public SiloAddress ExecutingSiloAddress => this.silo.SiloAddress;

        public void RegisterSystemTarget(ISystemTarget target)
        {
            this.silo.RegisterSystemTarget((SystemTarget)target);
        }

        public void UnRegisterSystemTarget(ISystemTarget target)
        {
            this.silo.UnregisterSystemTarget((SystemTarget)target);
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
                    return implictPubSub;
                default:
                    return null;
            }
        }

        public IConsistentRingProviderForGrains GetConsistentRingProvider(int mySubRangeIndex, int numSubRanges)
        {
            return new EquallyDividedRangeRingProvider(this.consistentRingProvider, mySubRangeIndex, numSubRanges);
        }
        
        public async Task<IPersistentStreamPullingManager> InitializePullingAgents(
            string streamProviderName,
            IQueueAdapterFactory adapterFactory,
            IQueueAdapter queueAdapter,
            PersistentStreamProviderConfig config)
        {
            IStreamQueueBalancer queueBalancer = StreamQueueBalancerFactory.Create(
                config.BalancerType, streamProviderName, this.silo.LocalSiloStatusOracle, this.silo.OrleansConfig, this, adapterFactory.GetStreamQueueMapper(), config.SiloMaturityPeriod);
            var managerId = GrainId.NewSystemTargetGrainIdByTypeCode(Constants.PULLING_AGENTS_MANAGER_SYSTEM_TARGET_TYPE_CODE);
            var manager = new PersistentStreamPullingManager(managerId, streamProviderName, this, this.PubSub(config.PubSubType), adapterFactory, queueBalancer, config);
            this.RegisterSystemTarget(manager);
            // Init the manager only after it was registered locally.
            var pullingAgentManager = manager.AsReference<IPersistentStreamPullingManager>();
            // Need to call it as a grain reference though.
            await pullingAgentManager.Initialize(queueAdapter.AsImmutable());
            return pullingAgentManager;
        }

        /// <inheritdoc />
        public string ExecutingEntityIdentity() => runtimeClient.ExecutingEntityIdentity();

        /// <inheritdoc />
        public StreamDirectory GetStreamDirectory() => runtimeClient.GetStreamDirectory();

        /// <inheritdoc />
        public Task<Tuple<TExtension, TExtensionInterface>> BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc) where TExtension : IGrainExtension where TExtensionInterface : IGrainExtension
        {
            return runtimeClient.BindExtension<TExtension, TExtensionInterface>(newExtensionFunc);
        }
    }
}

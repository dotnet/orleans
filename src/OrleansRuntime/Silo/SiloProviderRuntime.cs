using System;
using System.Threading.Tasks;

using Orleans.Concurrency;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.Scheduler;
using Orleans.Streams;

namespace Orleans.Runtime.Providers
{
    internal class SiloProviderRuntime : ISiloSideStreamProviderRuntime
    {
        private readonly SiloInitializationParameters siloDetails;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly OrleansTaskScheduler scheduler;
        private readonly ActivationDirectory activationDirectory;
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
            SiloInitializationParameters siloDetails,
            GlobalConfiguration config,
            IGrainFactory grainFactory,
            IConsistentRingProvider consistentRingProvider,
            ISiloRuntimeClient runtimeClient,
            IServiceProvider serviceProvider,
            ImplicitStreamSubscriberTable implicitStreamSubscriberTable,
            ISiloStatusOracle siloStatusOracle,
            OrleansTaskScheduler scheduler,
            ActivationDirectory activationDirectory)
        {
            this.siloDetails = siloDetails;
            this.siloStatusOracle = siloStatusOracle;
            this.scheduler = scheduler;
            this.activationDirectory = activationDirectory;
            this.consistentRingProvider = consistentRingProvider;
            this.runtimeClient = runtimeClient;
            this.ServiceId = config.ServiceId;
            this.SiloIdentity = siloDetails.SiloAddress.ToLongString();
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

        public SiloAddress ExecutingSiloAddress => this.siloStatusOracle.SiloAddress;

        public void RegisterSystemTarget(ISystemTarget target)
        {
            var systemTarget = target as SystemTarget;
            if (systemTarget == null) throw new ArgumentException($"Parameter must be of type {typeof(SystemTarget)}", nameof(target));
            scheduler.RegisterWorkContext(systemTarget.SchedulingContext);
            activationDirectory.RecordNewSystemTarget(systemTarget);
        }

        public void UnregisterSystemTarget(ISystemTarget target)
        {
            var systemTarget = target as SystemTarget;
            if (systemTarget == null) throw new ArgumentException($"Parameter must be of type {typeof(SystemTarget)}", nameof(target));
            activationDirectory.RemoveSystemTarget(systemTarget);
            scheduler.UnregisterWorkContext(systemTarget.SchedulingContext);
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
            IStreamQueueBalancer queueBalancer = StreamQueueBalancerFactory.Create(config.BalancerType, streamProviderName, this.siloStatusOracle, this.siloDetails.ClusterConfig, this, adapterFactory.GetStreamQueueMapper(), config.SiloMaturityPeriod);
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
        public string ExecutingEntityIdentity() => runtimeClient.CurrentActivationIdentity;

        /// <inheritdoc />
        public StreamDirectory GetStreamDirectory()
        {
            if (runtimeClient.CurrentActivationData == null)
            {
                throw new InvalidOperationException(
                    String.Format("Trying to get a Stream or send a stream message on a silo not from within grain and not from within system target (CurrentActivationData is null) "
                        + "RuntimeContext.Current={0} TaskScheduler.Current={1}",
                        RuntimeContext.Current == null ? "null" : RuntimeContext.Current.ToString(),
                        TaskScheduler.Current));
            }
            return runtimeClient.GetStreamDirectory();
        }

        /// <inheritdoc />
        public Task<Tuple<TExtension, TExtensionInterface>> BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc) where TExtension : IGrainExtension where TExtensionInterface : IGrainExtension
        {
            return runtimeClient.BindExtension<TExtension, TExtensionInterface>(newExtensionFunc);
        }
    }
}

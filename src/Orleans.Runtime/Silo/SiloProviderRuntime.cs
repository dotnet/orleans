using System;
using System.Threading.Tasks;

using Orleans.Concurrency;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.Scheduler;
using Orleans.Streams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Orleans.Runtime.Providers
{
    internal class SiloProviderRuntime : ISiloSideStreamProviderRuntime
    {
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly OrleansTaskScheduler scheduler;
        private readonly ActivationDirectory activationDirectory;
        private readonly IConsistentRingProvider consistentRingProvider;
        private readonly ISiloRuntimeClient runtimeClient;
        private readonly IStreamPubSub grainBasedPubSub;
        private readonly IStreamPubSub implictPubSub;
        private readonly IStreamPubSub combinedGrainBasedAndImplicitPubSub;
        private readonly ILoggerFactory loggerFactory;

        public IGrainFactory GrainFactory => this.runtimeClient.InternalGrainFactory;
        public IServiceProvider ServiceProvider => this.runtimeClient.ServiceProvider;

        public Guid ServiceId { get; }
        public string SiloIdentity { get; }

        public SiloProviderRuntime(
            ILocalSiloDetails siloDetails,
            IOptions<SiloOptions> siloOptions,
            IConsistentRingProvider consistentRingProvider,
            ISiloRuntimeClient runtimeClient,
            ImplicitStreamSubscriberTable implicitStreamSubscriberTable,
            ISiloStatusOracle siloStatusOracle,
            OrleansTaskScheduler scheduler,
            ActivationDirectory activationDirectory,
            ILoggerFactory loggerFactory)
        {
            this.loggerFactory = loggerFactory;
            this.siloStatusOracle = siloStatusOracle;
            this.scheduler = scheduler;
            this.activationDirectory = activationDirectory;
            this.consistentRingProvider = consistentRingProvider;
            this.runtimeClient = runtimeClient;
            this.ServiceId = siloOptions.Value.ServiceId;
            this.SiloIdentity = siloDetails.SiloAddress.ToLongString();

            this.grainBasedPubSub = new GrainBasedPubSubRuntime(this.GrainFactory);
            var tmp = new ImplicitStreamPubSub(this.runtimeClient.InternalGrainFactory, implicitStreamSubscriberTable);
            this.implictPubSub = tmp;
            this.combinedGrainBasedAndImplicitPubSub = new StreamPubSubImpl(this.grainBasedPubSub, tmp);
        }

        public SiloAddress ExecutingSiloAddress => this.siloStatusOracle.SiloAddress;

        public void RegisterSystemTarget(ISystemTarget target)
        {
            var systemTarget = target as SystemTarget;
            if (systemTarget == null) throw new ArgumentException($"Parameter must be of type {typeof(SystemTarget)}", nameof(target));
            systemTarget.RuntimeClient = this.runtimeClient;
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
            return new EquallyDividedRangeRingProvider(this.consistentRingProvider, this.loggerFactory, mySubRangeIndex, numSubRanges);
        }

        public async Task<IPersistentStreamPullingManager> InitializePullingAgents(
            string streamProviderName,
            IQueueAdapterFactory adapterFactory,
            IQueueAdapter queueAdapter,
            PersistentStreamProviderConfig config,
            IProviderConfiguration providerConfig)
        {
            IStreamQueueBalancer queueBalancer = CreateQueueBalancer(config, streamProviderName);
            var managerId = GrainId.NewSystemTargetGrainIdByTypeCode(Constants.PULLING_AGENTS_MANAGER_SYSTEM_TARGET_TYPE_CODE);
            var manager = new PersistentStreamPullingManager(managerId, streamProviderName, this, this.PubSub(config.PubSubType), adapterFactory, queueBalancer, config, providerConfig, this.loggerFactory);
            this.RegisterSystemTarget(manager);
            // Init the manager only after it was registered locally.
            var pullingAgentManager = manager.AsReference<IPersistentStreamPullingManager>();
            // Need to call it as a grain reference though.
            await pullingAgentManager.Initialize(queueAdapter.AsImmutable());
            return pullingAgentManager;
        }

        private IStreamQueueBalancer CreateQueueBalancer(PersistentStreamProviderConfig config, string streamProviderName)
        {
            //default type is ConsistentRingBalancer
            if (config.BalancerType == null)
                config.BalancerType = StreamQueueBalancerType.ConsistentRingBalancer;
            try
            {
                var balancer = this.ServiceProvider.GetRequiredService(config.BalancerType) as IStreamQueueBalancer;
                if (balancer == null)
                    throw new ArgumentOutOfRangeException("balancerType", $"Configured BalancerType isn't a type which implements IStreamQueueBalancer. BalancerType: {config.BalancerType}, StreamProvider: {streamProviderName}");
                return balancer;
            }
            catch (Exception e)
            {
                string error = $"Unsupported balancerType for stream provider. BalancerType: {config.BalancerType}, StreamProvider: {streamProviderName}, Exception: {e}";
                throw new ArgumentOutOfRangeException("balancerType", error);
            }
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

using System;
using System.Threading.Tasks;

using Orleans.Concurrency;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.Scheduler;
using Orleans.Streams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace Orleans.Runtime.Providers
{
    internal class SiloProviderRuntime : ISiloSideStreamProviderRuntime
    {
        private readonly OrleansTaskScheduler scheduler;
        private readonly ActivationDirectory activationDirectory;
        private readonly IConsistentRingProvider consistentRingProvider;
        private readonly ISiloRuntimeClient runtimeClient;
        private readonly IStreamPubSub grainBasedPubSub;
        private readonly IStreamPubSub implictPubSub;
        private readonly IStreamPubSub combinedGrainBasedAndImplicitPubSub;
        private readonly ILoggerFactory loggerFactory;
        private readonly ILocalSiloDetails siloDetails;
        private readonly ILogger logger;
        public IGrainFactory GrainFactory => this.runtimeClient.InternalGrainFactory;
        public IServiceProvider ServiceProvider => this.runtimeClient.ServiceProvider;

        public SiloProviderRuntime(
            IConsistentRingProvider consistentRingProvider,
            ISiloRuntimeClient runtimeClient,
            ImplicitStreamSubscriberTable implicitStreamSubscriberTable,
            OrleansTaskScheduler scheduler,
            ActivationDirectory activationDirectory,
            ILoggerFactory loggerFactory,
            ILocalSiloDetails siloDetails)
        {
            this.loggerFactory = loggerFactory;
            this.siloDetails = siloDetails;
            this.scheduler = scheduler;
            this.activationDirectory = activationDirectory;
            this.consistentRingProvider = consistentRingProvider;
            this.runtimeClient = runtimeClient;
            this.logger = this.loggerFactory.CreateLogger<SiloProviderRuntime>();
            this.grainBasedPubSub = new GrainBasedPubSubRuntime(this.GrainFactory);
            var tmp = new ImplicitStreamPubSub(this.runtimeClient.InternalGrainFactory, implicitStreamSubscriberTable);
            this.implictPubSub = tmp;
            this.combinedGrainBasedAndImplicitPubSub = new StreamPubSubImpl(this.grainBasedPubSub, tmp);
        }

        public void RegisterSystemTarget(ISystemTarget target)
        {
            var systemTarget = target as SystemTarget;
            if (systemTarget == null) throw new ArgumentException($"Parameter must be of type {typeof(SystemTarget)}", nameof(target));
            systemTarget.RuntimeClient = this.runtimeClient;
            scheduler.RegisterWorkContext(systemTarget);
            activationDirectory.RecordNewSystemTarget(systemTarget);
        }

        public void UnregisterSystemTarget(ISystemTarget target)
        {
            var systemTarget = target as SystemTarget;
            if (systemTarget == null) throw new ArgumentException($"Parameter must be of type {typeof(SystemTarget)}", nameof(target));
            activationDirectory.RemoveSystemTarget(systemTarget);
            scheduler.UnregisterWorkContext(systemTarget);
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
            IQueueAdapter queueAdapter)
        {
            IStreamQueueBalancer queueBalancer = CreateQueueBalancer(streamProviderName);
            var managerId = SystemTargetGrainId.Create(Constants.StreamPullingAgentManagerType, this.siloDetails.SiloAddress, streamProviderName);
            var pubsubOptions = this.ServiceProvider.GetOptionsByName<StreamPubSubOptions>(streamProviderName);
            var pullingAgentOptions = this.ServiceProvider.GetOptionsByName<StreamPullingAgentOptions>(streamProviderName);
            var manager = new PersistentStreamPullingManager(
                managerId,
                streamProviderName,
                this,
                this.PubSub(pubsubOptions.PubSubType),
                adapterFactory,
                queueBalancer,
                pullingAgentOptions,
                this.loggerFactory,
                this.siloDetails.SiloAddress);
            this.RegisterSystemTarget(manager);
            // Init the manager only after it was registered locally.
            var pullingAgentManager = manager.AsReference<IPersistentStreamPullingManager>();
            // Need to call it as a grain reference though.
            await pullingAgentManager.Initialize(queueAdapter.AsImmutable());
            return pullingAgentManager;
        }

        private IStreamQueueBalancer CreateQueueBalancer(string streamProviderName)
        {
            try
            {
                var balancer = this.ServiceProvider.GetServiceByName<IStreamQueueBalancer>(streamProviderName)??this.ServiceProvider.GetService<IStreamQueueBalancer>();
                if (balancer == null)
                    throw new ArgumentOutOfRangeException("balancerType", $"Cannot create stream queue balancer for StreamProvider: {streamProviderName}.Please configure your stream provider with a queue balancer.");
                this.logger.LogInformation($"Successfully created queue balancer of type {balancer.GetType()} for stream provider {streamProviderName}");
                return balancer;
            }
            catch (Exception e)
            {
                string error = $"Cannot create stream queue balancer for StreamProvider: {streamProviderName}, Exception: {e}. Please configure your stream provider with a queue balancer.";
                throw new ArgumentOutOfRangeException("balancerType", error);
            }
        }

        /// <inheritdoc />
        public string ExecutingEntityIdentity() => runtimeClient.CurrentActivationIdentity;

        /// <inheritdoc />
        public StreamDirectory GetStreamDirectory()
        {
            return runtimeClient.GetStreamDirectory();
        }

        /// <inheritdoc />
        public Task<Tuple<TExtension, TExtensionInterface>> BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc) where TExtension : IGrainExtension where TExtensionInterface : IGrainExtension
        {
            return runtimeClient.BindExtension<TExtension, TExtensionInterface>(newExtensionFunc);
        }
    }
}

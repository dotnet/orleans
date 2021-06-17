using System;
using System.Threading.Tasks;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.Scheduler;
using Orleans.Streams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streams.Filtering;

namespace Orleans.Runtime.Providers
{
    internal class SiloStreamProviderRuntime : ISiloSideStreamProviderRuntime
    {
        private readonly IConsistentRingProvider consistentRingProvider;
        private readonly InsideRuntimeClient runtimeClient;
        private readonly IStreamPubSub grainBasedPubSub;
        private readonly IStreamPubSub implictPubSub;
        private readonly IStreamPubSub combinedGrainBasedAndImplicitPubSub;
        private readonly ILoggerFactory loggerFactory;
        private readonly ILocalSiloDetails siloDetails;
        private readonly IGrainContextAccessor grainContextAccessor;
        private readonly ILogger logger;
        private readonly StreamDirectory hostedClientStreamDirectory = new StreamDirectory();

        public IGrainFactory GrainFactory => this.runtimeClient.InternalGrainFactory;
        public IServiceProvider ServiceProvider => this.runtimeClient.ServiceProvider;

        public SiloStreamProviderRuntime(
            IConsistentRingProvider consistentRingProvider,
            InsideRuntimeClient runtimeClient,
            ImplicitStreamSubscriberTable implicitStreamSubscriberTable,
            OrleansTaskScheduler scheduler,
            ILoggerFactory loggerFactory,
            ILocalSiloDetails siloDetails,
            IGrainContextAccessor grainContextAccessor)
        {
            this.loggerFactory = loggerFactory;
            this.siloDetails = siloDetails;
            this.grainContextAccessor = grainContextAccessor;
            this.consistentRingProvider = consistentRingProvider;
            this.runtimeClient = runtimeClient;
            this.logger = this.loggerFactory.CreateLogger<SiloProviderRuntime>();
            this.grainBasedPubSub = new GrainBasedPubSubRuntime(this.GrainFactory);
            var tmp = new ImplicitStreamPubSub(this.runtimeClient.InternalGrainFactory, implicitStreamSubscriberTable);
            this.implictPubSub = tmp;
            this.combinedGrainBasedAndImplicitPubSub = new StreamPubSubImpl(this.grainBasedPubSub, tmp);
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

        public async Task<IPersistentStreamPullingManager> InitializePullingAgents(
            string streamProviderName,
            IQueueAdapterFactory adapterFactory,
            IQueueAdapter queueAdapter)
        {
            IStreamQueueBalancer queueBalancer = CreateQueueBalancer(streamProviderName);
            var managerId = SystemTargetGrainId.Create(Constants.StreamPullingAgentManagerType, this.siloDetails.SiloAddress, streamProviderName);
            var pubsubOptions = this.ServiceProvider.GetOptionsByName<StreamPubSubOptions>(streamProviderName);
            var pullingAgentOptions = this.ServiceProvider.GetOptionsByName<StreamPullingAgentOptions>(streamProviderName);
            var filter = this.ServiceProvider.GetServiceByName<IStreamFilter>(streamProviderName) ?? new NoOpStreamFilter();
            var manager = new PersistentStreamPullingManager(
                managerId,
                streamProviderName,
                this.PubSub(pubsubOptions.PubSubType),
                adapterFactory,
                queueBalancer,
                filter,
                pullingAgentOptions,
                this.loggerFactory,
                this.siloDetails.SiloAddress,
                queueAdapter);

            var catalog = this.ServiceProvider.GetRequiredService<Catalog>();
            catalog.RegisterSystemTarget(manager);

            // Init the manager only after it was registered locally.
            var pullingAgentManager = manager.AsReference<IPersistentStreamPullingManager>();

            // Need to call it as a grain reference though.
            await pullingAgentManager.Initialize();
            return pullingAgentManager;
        }

        private IStreamQueueBalancer CreateQueueBalancer(string streamProviderName)
        {
            try
            {
                var balancer = this.ServiceProvider.GetServiceByName<IStreamQueueBalancer>(streamProviderName) ??this.ServiceProvider.GetService<IStreamQueueBalancer>();
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
            if (RuntimeContext.CurrentGrainContext is { } activation)
            {
                var directory = activation.GetComponent<StreamDirectory>();
                if (directory is null)
                {
                    directory = activation.ActivationServices.GetRequiredService<StreamDirectory>();
                    activation.SetComponent(directory);
                }

                return directory;
            }

            return this.hostedClientStreamDirectory;
        }

        public (TExtension, TExtensionInterface) BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : TExtensionInterface
            where TExtensionInterface : IGrainExtension
        {
            return this.grainContextAccessor.GrainContext.GetComponent<IGrainExtensionBinder>().GetOrSetExtension<TExtension, TExtensionInterface>(newExtensionFunc);
        }
    }
}

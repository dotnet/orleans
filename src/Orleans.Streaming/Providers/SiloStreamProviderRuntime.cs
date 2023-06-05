using System;
using System.Threading.Tasks;
using Orleans.Runtime.ConsistentRing;
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

        public IGrainFactory GrainFactory => runtimeClient.InternalGrainFactory;
        public IServiceProvider ServiceProvider => runtimeClient.ServiceProvider;

        public SiloStreamProviderRuntime(
            IConsistentRingProvider consistentRingProvider,
            InsideRuntimeClient runtimeClient,
            ImplicitStreamSubscriberTable implicitStreamSubscriberTable,
            ILoggerFactory loggerFactory,
            ILocalSiloDetails siloDetails,
            IGrainContextAccessor grainContextAccessor)
        {
            this.loggerFactory = loggerFactory;
            this.siloDetails = siloDetails;
            this.grainContextAccessor = grainContextAccessor;
            this.consistentRingProvider = consistentRingProvider;
            this.runtimeClient = runtimeClient;
            logger = this.loggerFactory.CreateLogger<SiloProviderRuntime>();
            grainBasedPubSub = new GrainBasedPubSubRuntime(GrainFactory);
            var tmp = new ImplicitStreamPubSub(this.runtimeClient.InternalGrainFactory, implicitStreamSubscriberTable);
            implictPubSub = tmp;
            combinedGrainBasedAndImplicitPubSub = new StreamPubSubImpl(grainBasedPubSub, tmp);
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
            var managerId = SystemTargetGrainId.Create(Constants.StreamPullingAgentManagerType, siloDetails.SiloAddress, streamProviderName);
            var pubsubOptions = ServiceProvider.GetOptionsByName<StreamPubSubOptions>(streamProviderName);
            var pullingAgentOptions = ServiceProvider.GetOptionsByName<StreamPullingAgentOptions>(streamProviderName);
            var filter = ServiceProvider.GetServiceByName<IStreamFilter>(streamProviderName) ?? new NoOpStreamFilter();
            var manager = new PersistentStreamPullingManager(
                managerId,
                streamProviderName,
                PubSub(pubsubOptions.PubSubType),
                adapterFactory,
                queueBalancer,
                filter,
                pullingAgentOptions,
                loggerFactory,
                siloDetails.SiloAddress,
                queueAdapter);

            var catalog = ServiceProvider.GetRequiredService<Catalog>();
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
                var balancer = ServiceProvider.GetServiceByName<IStreamQueueBalancer>(streamProviderName) ??ServiceProvider.GetService<IStreamQueueBalancer>();
                if (balancer == null)
                    throw new ArgumentOutOfRangeException("balancerType", $"Cannot create stream queue balancer for StreamProvider: {streamProviderName}.Please configure your stream provider with a queue balancer.");
                logger.LogInformation(
                    "Successfully created queue balancer of type {BalancerType} for stream provider {StreamProviderName}",
                    balancer.GetType(),
                    streamProviderName);
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
            if (RuntimeContext.Current is { } activation)
            {
                var directory = activation.GetComponent<StreamDirectory>();
                if (directory is null)
                {
                    directory = activation.ActivationServices.GetRequiredService<StreamDirectory>();
                    activation.SetComponent(directory);
                }

                return directory;
            }

            return hostedClientStreamDirectory;
        }

        public (TExtension, TExtensionInterface) BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : class, TExtensionInterface
            where TExtensionInterface : class, IGrainExtension
        {
            if (grainContextAccessor.GrainContext is ActivationData activationData && activationData.IsStatelessWorker)
            {
                throw new InvalidOperationException($"The extension { typeof(TExtension) } cannot be bound to a Stateless Worker.");
            }

            return grainContextAccessor.GrainContext.GetComponent<IGrainExtensionBinder>().GetOrSetExtension<TExtension, TExtensionInterface>(newExtensionFunc);
        }
    }
}

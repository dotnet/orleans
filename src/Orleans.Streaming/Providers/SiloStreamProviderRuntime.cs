using System;
using System.Threading.Tasks;
using Orleans.Runtime.ConsistentRing;
using Orleans.Streams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streams.Filtering;
using Orleans.Internal;

namespace Orleans.Runtime.Providers
{
    internal partial class SiloStreamProviderRuntime : ISiloSideStreamProviderRuntime
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
            (var deliveryProvider, var queueReaderProvider) = CreateBackoffProviders(streamProviderName);
            var managerId = SystemTargetGrainId.Create(Constants.StreamPullingAgentManagerType, this.siloDetails.SiloAddress, streamProviderName);
            var pubsubOptions = this.ServiceProvider.GetOptionsByName<StreamPubSubOptions>(streamProviderName);
            var pullingAgentOptions = this.ServiceProvider.GetOptionsByName<StreamPullingAgentOptions>(streamProviderName);
            var filter = this.ServiceProvider.GetKeyedService<IStreamFilter>(streamProviderName) ?? new NoOpStreamFilter();
            var manager = new PersistentStreamPullingManager(
                managerId,
                streamProviderName,
                this.PubSub(pubsubOptions.PubSubType),
                adapterFactory,
                queueBalancer,
                filter,
                pullingAgentOptions,
                queueAdapter,
                deliveryProvider,
                queueReaderProvider,
                ServiceProvider.GetRequiredService<SystemTargetShared>());

            // Init the manager only after it was registered locally.
            var pullingAgentManager = manager.AsReference<IPersistentStreamPullingManager>();

            // Need to call it as a grain reference though.
            await pullingAgentManager.Initialize();
            return pullingAgentManager;
        }

        private (IBackoffProvider, IBackoffProvider) CreateBackoffProviders(string streamProviderName)
        {
            var deliveryProvider = (IBackoffProvider)ServiceProvider.GetKeyedService<IMessageDeliveryBackoffProvider>(streamProviderName) ??
                new ExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));

            var queueReaderProvider = (IBackoffProvider)ServiceProvider.GetKeyedService<IQueueReaderBackoffProvider>(streamProviderName) ??
                new ExponentialBackoff(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1));

            return new(deliveryProvider, queueReaderProvider);
        }

        private IStreamQueueBalancer CreateQueueBalancer(string streamProviderName)
        {
            try
            {
                var balancer = this.ServiceProvider.GetKeyedService<IStreamQueueBalancer>(streamProviderName) ??this.ServiceProvider.GetService<IStreamQueueBalancer>();
                if (balancer == null)
                    throw new ArgumentOutOfRangeException("balancerType", $"Cannot create stream queue balancer for StreamProvider: {streamProviderName}.Please configure your stream provider with a queue balancer.");
                LogInfoSuccessfullyCreatedQueueBalancer(balancer.GetType(), streamProviderName);
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

            return this.hostedClientStreamDirectory;
        }

        public (TExtension, TExtensionInterface) BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : class, TExtensionInterface
            where TExtensionInterface : class, IGrainExtension
        {
            if (this.grainContextAccessor.GrainContext is ActivationData activationData && activationData.IsStatelessWorker)
            {
                throw new InvalidOperationException($"The extension { typeof(TExtension) } cannot be bound to a Stateless Worker.");
            }

            return this.grainContextAccessor.GrainContext.GetComponent<IGrainExtensionBinder>().GetOrSetExtension<TExtension, TExtensionInterface>(newExtensionFunc);
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Successfully created queue balancer of type {BalancerType} for stream provider {StreamProviderName}"
        )]
        private partial void LogInfoSuccessfullyCreatedQueueBalancer(Type balancerType, string streamProviderName);
    }
}

using System;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    [Serializable]
    public enum PersistentStreamProviderState
    {
        None,
        Initialized,
        AgentsStarted,
        AgentsStopped,
    }

    [Serializable]
    public enum PersistentStreamProviderCommand
    {
        None,
        StartAgents,
        StopAgents,
        GetAgentsState,
        GetNumberRunningAgents,
        AdapterCommandStartRange = 10000,
        AdapterCommandEndRange = AdapterCommandStartRange + 9999,
        AdapterFactoryCommandStartRange = AdapterCommandEndRange + 1,
        AdapterFactoryCommandEndRange = AdapterFactoryCommandStartRange + 9999,
    }

    /// <summary>
    /// Persistent stream provider that uses an adapter for persistence
    /// </summary>
    /// <typeparam name="TAdapterFactory"></typeparam>
    public class PersistentStreamProvider<TAdapterFactory> : IInternalStreamProvider, IControllable
        where TAdapterFactory : IQueueAdapterFactory, new()
    {
        private Logger                  logger;
        private IQueueAdapterFactory    adapterFactory;
        private IStreamProviderRuntime  providerRuntime;
        private IQueueAdapter           queueAdapter;
        private IPersistentStreamPullingManager pullingAgentManager;
        private PersistentStreamProviderConfig myConfig;
        internal const string StartupStatePropertyName = "StartupState";
        internal const PersistentStreamProviderState StartupStateDefaultValue = PersistentStreamProviderState.AgentsStarted;
        private PersistentStreamProviderState startupState;
        private ProviderStateManager stateManager = new ProviderStateManager();

        public string Name { get; private set; }

        public bool IsRewindable { get { return queueAdapter.IsRewindable; } }

        // this is a workaround until an IServiceProvider instance is used in the Orleans client
        private class GrainFactoryServiceProvider : IServiceProvider
        {
            private IStreamProviderRuntime providerRuntime;
            public GrainFactoryServiceProvider(IStreamProviderRuntime providerRuntime)
            {
                this.providerRuntime = providerRuntime;
            }
            public object GetService(Type serviceType)
            {
                var service = providerRuntime.ServiceProvider?.GetService(serviceType);
                if (service != null)
                {
                    return service;
                }

                if (serviceType == typeof(IGrainFactory))
                {
                    return providerRuntime.GrainFactory;
                }

                return null;
            }
        }

        public async Task Init(string name, IProviderRuntime providerUtilitiesManager, IProviderConfiguration config)
        {
            if(!stateManager.PresetState(ProviderState.Initialized)) return;
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException("name");
            if (providerUtilitiesManager == null) throw new ArgumentNullException("providerUtilitiesManager");
            if (config == null) throw new ArgumentNullException("config");
            
            Name = name;
            providerRuntime = (IStreamProviderRuntime)providerUtilitiesManager;
            logger = providerRuntime.GetLogger(this.GetType().Name);
            adapterFactory = new TAdapterFactory();
            // Temporary change, but we need GrainFactory inside ServiceProvider for now, 
            // so will change it back as soon as we have an action item to add GrainFactory to ServiceProvider.
            adapterFactory.Init(config, Name, logger, new GrainFactoryServiceProvider(providerRuntime));
            queueAdapter = await adapterFactory.CreateAdapter();
            myConfig = new PersistentStreamProviderConfig(config);
            string startup;
            if (config.Properties.TryGetValue(StartupStatePropertyName, out startup))
            {
                if(!Enum.TryParse(startup, true, out startupState))
                    throw new ArgumentException(
                        String.Format("Unsupported value '{0}' for configuration parameter {1} of stream provider {2}.", startup, StartupStatePropertyName, config.Name));
            }
            else
                startupState = StartupStateDefaultValue;

            logger.Info("Initialized PersistentStreamProvider<{0}> with name {1}, Adapter {2} and config {3}, {4} = {5}.",
                typeof(TAdapterFactory).Name, 
                Name, 
                queueAdapter.Name,
                myConfig,
                StartupStatePropertyName, startupState);
            stateManager.CommitState();
        }

        public async Task Start()
        {
            if (!stateManager.PresetState(ProviderState.Started)) return;
            if (queueAdapter.Direction.Equals(StreamProviderDirection.ReadOnly) ||
                queueAdapter.Direction.Equals(StreamProviderDirection.ReadWrite))
            {
                var siloRuntime = providerRuntime as ISiloSideStreamProviderRuntime;
                if (siloRuntime != null)
                {
                    pullingAgentManager = await siloRuntime.InitializePullingAgents(Name, adapterFactory, queueAdapter, myConfig);

                    // TODO: No support yet for DeliveryDisabled, only Stopped and Started
                    if (startupState == PersistentStreamProviderState.AgentsStarted)
                        await pullingAgentManager.StartAgents();
                }
            }
            stateManager.CommitState();
        }

        public async Task Close()
        {
            if (!stateManager.PresetState(ProviderState.Closed)) return;
            var siloRuntime = providerRuntime as ISiloSideStreamProviderRuntime;
            if (siloRuntime != null)
            {
                await pullingAgentManager.Stop();
            }
            stateManager.CommitState();
        }

        public IAsyncStream<T> GetStream<T>(Guid id, string streamNamespace)
        {
            var streamId = StreamId.GetStreamId(id, Name, streamNamespace);
            return providerRuntime.GetStreamDirectory().GetOrAddStream<T>(
                streamId, () => new StreamImpl<T>(streamId, this, IsRewindable));
        }

        IInternalAsyncBatchObserver<T> IInternalStreamProvider.GetProducerInterface<T>(IAsyncStream<T> stream)
        {
            if (queueAdapter.Direction == StreamProviderDirection.ReadOnly)
            {
                throw new InvalidOperationException($"Stream provider {queueAdapter.Name} is ReadOnly.");
            }
            return new PersistentStreamProducer<T>((StreamImpl<T>)stream, providerRuntime, queueAdapter, IsRewindable);
        }

        IInternalAsyncObservable<T> IInternalStreamProvider.GetConsumerInterface<T>(IAsyncStream<T> streamId)
        {
            return GetConsumerInterfaceImpl(streamId);
        }

        private IInternalAsyncObservable<T> GetConsumerInterfaceImpl<T>(IAsyncStream<T> stream)
        {
            return new StreamConsumer<T>((StreamImpl<T>)stream, Name, providerRuntime, providerRuntime.PubSub(myConfig.PubSubType), IsRewindable);
        }

        public Task<object> ExecuteCommand(int command, object arg)
        {
            if (command >= (int)PersistentStreamProviderCommand.AdapterCommandStartRange &&
                command <= (int)PersistentStreamProviderCommand.AdapterCommandEndRange &&
                queueAdapter is IControllable)
            {
                return ((IControllable)queueAdapter).ExecuteCommand(command, arg);
            }

            if (command >= (int)PersistentStreamProviderCommand.AdapterFactoryCommandStartRange &&
                command <= (int)PersistentStreamProviderCommand.AdapterFactoryCommandEndRange &&
                adapterFactory is IControllable)
            {
                return ((IControllable)adapterFactory).ExecuteCommand(command, arg);
            }
            
            if (pullingAgentManager != null)
            {
                return pullingAgentManager.ExecuteCommand((PersistentStreamProviderCommand)command, arg);
            }

            logger.Warn(0, String.Format("Got command {0} with arg {1}, but PullingAgentManager is not initialized yet. Ignoring the command.", 
                (PersistentStreamProviderCommand)command, arg != null ? arg.ToString() : "null"));
            throw new ArgumentException("PullingAgentManager is not initialized yet.");
        }
    }
}

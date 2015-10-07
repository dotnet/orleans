/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
        private const string STARTUP_STATE = "StartupState";
        private PersistentStreamProviderState startupState;

        public string                   Name { get; private set; }
        public bool IsRewindable { get { return queueAdapter.IsRewindable; } }

        public async Task Init(string name, IProviderRuntime providerUtilitiesManager, IProviderConfiguration config)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException("name");
            if (providerUtilitiesManager == null) throw new ArgumentNullException("providerUtilitiesManager");
            if (config == null) throw new ArgumentNullException("config");
            
            Name = name;
            providerRuntime = (IStreamProviderRuntime)providerUtilitiesManager;
            logger = providerRuntime.GetLogger(this.GetType().Name);
            adapterFactory = new TAdapterFactory();
            adapterFactory.Init(config, Name, logger);
            queueAdapter = await adapterFactory.CreateAdapter();
            myConfig = new PersistentStreamProviderConfig(config);
            string startup;
            if (config.Properties.TryGetValue(STARTUP_STATE, out startup))
            {
                if(!Enum.TryParse(startup, true, out startupState))
                    throw new ArgumentException(
                        String.Format("Unsupported value '{0}' for configuration parameter {1} of stream provider {2}.", startup, STARTUP_STATE, config.Name));
            }
            else
                startupState = PersistentStreamProviderState.AgentsStarted;

            logger.Info("Initialized PersistentStreamProvider<{0}> with name {1}, Adapter {2} and config {3}, {4} = {5}.",
                typeof(TAdapterFactory).Name, 
                Name, 
                queueAdapter.Name,
                myConfig,
                STARTUP_STATE, startupState);
        }

        public async Task Start()
        {
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
        }

        public async Task Close()
        {
            var siloRuntime = providerRuntime as ISiloSideStreamProviderRuntime;
            if (siloRuntime != null)
            {
                await pullingAgentManager.Stop();
            }
        }

        public IAsyncStream<T> GetStream<T>(Guid id, string streamNamespace)
        {
            var streamId = StreamId.GetStreamId(id, Name, streamNamespace);
            return providerRuntime.GetStreamDirectory().GetOrAddStream<T>(
                streamId, () => new StreamImpl<T>(streamId, this, IsRewindable));
        }

        IInternalAsyncBatchObserver<T> IInternalStreamProvider.GetProducerInterface<T>(IAsyncStream<T> stream)
        {
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

#if NETSTANDARD
using Microsoft.Azure.EventHubs;
#else
using Microsoft.ServiceBus.Messaging;
#endif
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Setting class for EHGeneratorStreamProvider
    /// </summary>
    public class EventHubGeneratorStreamProviderSettings : EventHubStreamProviderSettings
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="providerName"></param>
        public EventHubGeneratorStreamProviderSettings(string providerName)
            :base(providerName)
        { }

        /// <summary>
        /// StreamDataGeneratorTypeName
        /// </summary>
        public static string StreamDataGeneratorTypeName = nameof(StreamDataGeneratorType);
        /// <summary>
        /// DefaultStreamDataGeneratorType
        /// </summary>
        public static Type DefaultStreamDataGeneratorType = typeof(SimpleStreamEventDataGenerator);
        private Type streamDataGeneratorType;
        /// <summary>
        /// type info for stream data generator
        /// </summary>
        public Type StreamDataGeneratorType
        {
            get { return streamDataGeneratorType ?? DefaultStreamDataGeneratorType; }
            set { streamDataGeneratorType = value; }
        }
        /// <summary>
        /// Populate data generating config from provider config
        /// </summary>
        /// <param name="providerConfiguration"></param>
        public void PopulateDataGeneratingConfigFromProviderConfig(IProviderConfiguration providerConfiguration)
        {
            this.streamDataGeneratorType = providerConfiguration.GetTypeProperty(StreamDataGeneratorTypeName, DefaultStreamDataGeneratorType);
        }

        /// <summary>
        /// Write data generating config to a property bag
        /// </summary>
        /// <param name="properties"></param>
        public void WriteDataGeneratingConfig(Dictionary<string, string> properties)
        {
            properties.Add(StreamDataGeneratorTypeName, this.StreamDataGeneratorType.AssemblyQualifiedName);
        }
    }
    /// <summary>
    /// This is a persistent stream provider that generates it's own events rather than reading them from Eventhub.
    /// This is primarily for test purposes.
    ///  </summary>
    public class EventDataGeneratorStreamProvider : PersistentStreamProvider<EventDataGeneratorStreamProvider.AdapterFactory>
    {
        /// <summary>
        /// EHGeneratorStreamProvider.AdpaterFactory
        /// </summary>
        public class AdapterFactory : EventHubAdapterFactory, IControllable
        {
            private EventHubGeneratorStreamProviderSettings ehGeneratorSettings;
            /// <summary>
            /// Init method
            /// </summary>
            /// <param name="providerCfg"></param>
            /// <param name="providerName"></param>
            /// <param name="log"></param>
            /// <param name="svcProvider"></param>
            public override void Init(IProviderConfiguration providerCfg, string providerName, Logger log, IServiceProvider svcProvider)
            {
                this.EventHubReceiverFactory = this.EHGeneratorReceiverFactory;
                this.ehGeneratorSettings = new EventHubGeneratorStreamProviderSettings(providerName);
                this.ehGeneratorSettings.PopulateDataGeneratingConfigFromProviderConfig(providerCfg);
                base.Init(providerCfg, providerName, log, svcProvider);
            }

            private Task<IEventHubReceiver> EHGeneratorReceiverFactory(EventHubPartitionSettings settings, string offset, Logger logger)
            {
                var generator = new EventHubPartitionDataGenerator(logger, this.serviceProvider.GetRequiredService<SerializationManager>(), this.ehGeneratorSettings);
                var generatorReceiver = new EventHubPartitionGeneratorReceiver(generator);
                return Task.FromResult<IEventHubReceiver>(generatorReceiver);
            }

            private void RandomlyPlaceStreamToQueue(StreamRandomPlacementArg args)
            {
                if (args == null)
                    return;
                int randomNumber = args.RandomNumber;
                IStreamIdentity streamId = args.StreamId;
                var allQueueInTheCluster = (this.EventHubQueueMapper as EventHubQueueMapper)?.GetAllQueues().OrderBy(queueId => queueId.ToString());

                if (allQueueInTheCluster != null)
                {
                    //every agent receive the same random number, do a mod on queue count, get the same random queue to assign stream to.
                    int randomQueue = randomNumber % allQueueInTheCluster.Count();
                    var queueToAssign = allQueueInTheCluster.ToList()[randomQueue];
                    EventHubAdapterReceiver receiverToAssign;
                    if (this.EventHubReceivers.TryGetValue(queueToAssign, out receiverToAssign))
                    {
                        receiverToAssign.ConfigureDataGeneratorForStream(streamId);
                        logger.Info($"Stream {streamId.Namespace}-{streamId.Guid.ToString()} is assigned to queue {queueToAssign.ToString()}");
                    }
                } else
                {
                    logger.Info("Cannot get queues in the cluster, current streamQueueMapper is not EventHubQueueMapper");
                }
            }

            private void StopProducingOnStream(IStreamIdentity streamId)
            {
                foreach (var ehReceiver in this.EventHubReceivers)
                {
                    //if the stream is assigned to this receiver/queue, then it will ask the data generator to stop producing
                    ehReceiver.Value.StopProducingOnStream(streamId);
                }
            }

            #region IControllable interface
            /// <summary>
            /// Commands for IControllable
            /// </summary>
            public enum Commands
            {
                /// <summary>
                /// Command for Randomly_Place_Stream_To_Queue
                /// </summary>
                Randomly_Place_Stream_To_Queue = (int)PersistentStreamProviderCommand.AdapterFactoryCommandStartRange + 4,
                /// <summary>
                /// Command for Stop_Producing_On_Stream
                /// </summary>
                Stop_Producing_On_Stream = (int)PersistentStreamProviderCommand.AdapterFactoryCommandStartRange + 5
            }

            /// <summary>
            /// Args for RandomlyPlaceStreamToQueue method
            /// </summary>
            [Serializable]
            public class StreamRandomPlacementArg
            {
                /// <summary>
                /// StreamId
                /// </summary>
                public IStreamIdentity StreamId { get; set; }

                /// <summary>
                /// A random number
                /// </summary>
                public int RandomNumber { get; set; }

                /// <summary>
                /// Constructor
                /// </summary>
                /// <param name="streamId"></param>
                /// <param name="randomNumber"></param>
                public StreamRandomPlacementArg(IStreamIdentity streamId, int randomNumber)
                {
                    this.StreamId = streamId;
                    this.RandomNumber = randomNumber;
                }
            }

            /// <summary>
            /// Execute Command 
            /// </summary>
            /// <param name="command"></param>
            /// <param name="arg"></param>
            /// <returns></returns>
            public virtual Task<object> ExecuteCommand(int command, object arg)
            {
                switch (command)
                {
                    case (int)Commands.Randomly_Place_Stream_To_Queue:
                        this.RandomlyPlaceStreamToQueue(arg as StreamRandomPlacementArg);
                        break;
                    case (int)Commands.Stop_Producing_On_Stream:
                        this.StopProducingOnStream(arg as IStreamIdentity);
                        break;
                    default: break;
                        
                }
                return Task.FromResult((object)true);
            }
#endregion
        }
    }
}

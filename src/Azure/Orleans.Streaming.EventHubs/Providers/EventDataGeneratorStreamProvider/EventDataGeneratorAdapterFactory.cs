using System;
using System.Linq;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Statistics;
using Orleans.Streams;

namespace Orleans.Streaming.EventHubs.Testing
{
    /// <summary>
    /// This is a persistent stream provider adapter that generates it's own events rather than reading them from Eventhub.
    /// This is primarily for test purposes.
    ///  </summary>
    public class EventDataGeneratorAdapterFactory : EventHubAdapterFactory, IControllable
    {
        private readonly EventDataGeneratorStreamOptions ehGeneratorOptions;

        public EventDataGeneratorAdapterFactory(
            string name,
            EventDataGeneratorStreamOptions options,
            EventHubOptions ehOptions,
            EventHubReceiverOptions receiverOptions,
            EventHubStreamCachePressureOptions cacheOptions,
            StreamCacheEvictionOptions evictionOptions,
            StreamStatisticOptions statisticOptions,
            IEventHubDataAdapter dataAdapter,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory,
            IHostEnvironmentStatistics hostEnvironmentStatistics)
            : base(name, ehOptions, receiverOptions, cacheOptions, evictionOptions, statisticOptions, dataAdapter, serviceProvider, loggerFactory, hostEnvironmentStatistics)
        {
            this.ehGeneratorOptions = options;
        }

        public override void Init()
        {
            this.EventHubReceiverFactory = this.EHGeneratorReceiverFactory;
            base.Init();
        }

        /// <inheritdoc/>
        protected override void InitEventHubClient()
        {
            //do nothing, EventDataGeneratorStreamProvider doesn't need connection with EventHubClient
        }

        /// <summary>
        /// Generate mocked eventhub partition Ids from EventHubGeneratorStreamProviderSettings
        /// </summary>
        /// <returns></returns>
        protected override Task<string[]> GetPartitionIdsAsync()
        {
            return Task.FromResult(GenerateEventHubPartitions(this.ehGeneratorOptions.EventHubPartitionCount));
        }

        private IEventHubReceiver EHGeneratorReceiverFactory(EventHubPartitionSettings settings, string offset, ILogger logger)
        {
            var streamGeneratorFactory = this.serviceProvider.GetServiceByName<Func<StreamId, IStreamDataGenerator<EventData>>>(this.Name)
                ?? SimpleStreamEventDataGenerator.CreateFactory(this.serviceProvider);
            var generator = new EventHubPartitionDataGenerator(this.ehGeneratorOptions, streamGeneratorFactory, logger);
            return new EventHubPartitionGeneratorReceiver(generator);
        }

        private void RandomlyPlaceStreamToQueue(StreamRandomPlacementArg args)
        {
            if (args == null)
                return;

            var allQueueInTheCluster = EventHubQueueMapper?.GetAllQueues();
            if (allQueueInTheCluster != null)
            {
                var allQueues = allQueueInTheCluster as QueueId[] ?? allQueueInTheCluster.ToArray();
                //every agent receive the same random number, do a mod on queue count, get the same random queue to assign stream to.
                var queueToAssign = allQueues[args.RandomNumber % allQueues.Length];
                if (EventHubReceivers.TryGetValue(queueToAssign, out var receiverToAssign))
                {
                    receiverToAssign.ConfigureDataGeneratorForStream(args.StreamId);
                    logger.LogInformation("Stream {StreamId} is assigned to queue {QueueId}", args.StreamId, queueToAssign);
                }
            }
            else
            {
                logger.LogInformation("Cannot get queues in the cluster, current streamQueueMapper is not EventHubQueueMapper");
            }
        }

        private void StopProducingOnStream(StreamId streamId)
        {
            foreach (var ehReceiver in this.EventHubReceivers)
            {
                //if the stream is assigned to this receiver/queue, then it will ask the data generator to stop producing
                ehReceiver.Value.StopProducingOnStream(streamId);
            }
        }

        public static string[] GenerateEventHubPartitions(int partitionCount)
        {
            var partitions = new string[partitionCount];
            for (int i = 0; i < partitions.Length; i++)
                partitions[i] = $"partition-{i}";
            return partitions;
        }

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
        [GenerateSerializer]
        public class StreamRandomPlacementArg
        {
            /// <summary>
            /// StreamId
            /// </summary>
            [Id(0)]
            public StreamId StreamId { get; set; }

            /// <summary>
            /// A random number
            /// </summary>
            [Id(1)]
            public int RandomNumber { get; set; }

            /// <summary>
            /// Constructor
            /// </summary>
            /// <param name="streamId"></param>
            /// <param name="randomNumber"></param>
            public StreamRandomPlacementArg(StreamId streamId, int randomNumber)
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
                    this.StopProducingOnStream((StreamId) arg);
                    break;
                default: break;

            }
            return Task.FromResult((object)true);
        }

        public new static EventDataGeneratorAdapterFactory Create(IServiceProvider services, string name)
        {
            var generatorOptions= services.GetOptionsByName<EventDataGeneratorStreamOptions>(name);
            var ehOptions = services.GetOptionsByName<EventHubOptions>(name);
            var receiverOptions = services.GetOptionsByName<EventHubReceiverOptions>(name);
            var cacheOptions = services.GetOptionsByName<EventHubStreamCachePressureOptions>(name);
            var statisticOptions = services.GetOptionsByName<StreamStatisticOptions>(name);
            var evictionOptions = services.GetOptionsByName<StreamCacheEvictionOptions>(name);
            IEventHubDataAdapter dataAdapter = services.GetServiceByName<IEventHubDataAdapter>(name)
                ?? services.GetService<IEventHubDataAdapter>()
                ?? ActivatorUtilities.CreateInstance<EventHubDataAdapter>(services);
            var factory = ActivatorUtilities.CreateInstance<EventDataGeneratorAdapterFactory>(services, name, generatorOptions, ehOptions, receiverOptions, cacheOptions, 
                evictionOptions, statisticOptions, dataAdapter);
            factory.Init();
            return factory;
        }
    }
}

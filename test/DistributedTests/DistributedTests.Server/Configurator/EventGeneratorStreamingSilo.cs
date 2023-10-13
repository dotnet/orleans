using System.CommandLine;
using DistributedTests.GrainInterfaces;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Streams;

namespace DistributedTests.Server.Configurator
{
    public class EventGeneratorStreamingSilo : ISiloConfigurator<EventGeneratorStreamingSilo.Parameter>
    {
        public class Parameter
        {
            public StreamPubSubType Type { get; set; }

            public int StreamsPerQueue { get; set; }

            public int QueueCount { get; set; }

            public int BatchSize { get; set; }

            public int Wait { get; set; }

            public int Duration { get; set; }
        }

        public string Name => nameof(EventGeneratorStreamingSilo);

        public List<Option> Options => new()
        {
            OptionHelper.CreateOption<StreamPubSubType>("--type", defaultValue: StreamPubSubType.ExplicitGrainBasedAndImplicit),
            OptionHelper.CreateOption<int>("--streamsPerQueue", defaultValue: 1000),
            OptionHelper.CreateOption<int>("--queueCount", defaultValue: 8),
            OptionHelper.CreateOption<int>("--batchSize", defaultValue: 5),
            OptionHelper.CreateOption<int>("--wait", "initial wait, in seconds, before starting generating events", defaultValue: 30),
            OptionHelper.CreateOption<int>("--duration", "duration, in seconds, of the run", defaultValue: 120),
        };

        public void Configure(ISiloBuilder siloBuilder, Parameter parameters)
        {
            var generatorOptions = new TimeBoundEventGeneratorConfig
            {
                NumberOfStreams = parameters.StreamsPerQueue,
                StreamNamespace = StreamingConstants.StreamingNamespace,
                StartTime = DateTime.UtcNow.AddSeconds(parameters.Wait),
                EndTime = DateTime.UtcNow.AddSeconds(parameters.Duration),
            };

            siloBuilder
                .AddMemoryGrainStorage("PubSubStore")
                .ConfigureServices(svc =>
                {
                    svc.AddOptions<ReportingOptions>().Configure(options =>
                    {
                        options.ReportAt = generatorOptions.EndTime.AddSeconds(parameters.Wait);
                        options.Duration = parameters.Duration;
                    });
                })
                .ConfigureServices(services => services.AddSingletonNamedService<IStreamGeneratorConfig>(StreamingConstants.StreamingProvider, (s, n) => generatorOptions))
                .AddPersistentStreams(
                    StreamingConstants.StreamingProvider,
                    GeneratorAdapterFactory.Create,
                    b =>
                    {
                        b.ConfigurePullingAgent(ob => ob.Configure(options => { options.BatchContainerBatchSize = parameters.BatchSize; }));
                        b.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = parameters.QueueCount));
                        b.UseConsistentRingQueueBalancer();
                        b.ConfigureStreamPubSub(parameters.Type);
                    });
        }
    }

    public class TimeBoundEventGeneratorConfig : IStreamGeneratorConfig
    {
        public Type StreamGeneratorType => typeof(TimeBoundEventGenerator);

        public string StreamNamespace { get; set; }

        public int NumberOfStreams { get; set; }

        public Type PayloadType { get; set; } = typeof(object);

        public DateTime StartTime { get; set; }

        public DateTime EndTime { get; set; }
    }

    public class TimeBoundEventGenerator : IStreamGenerator
    {
        private DateTime _startTime;
        private DateTime _endTime;
        private readonly List<StreamId> _streamIds = new List<StreamId>();
        private int _sequenceId = 0;
        private object _payload;

        public void Configure(IServiceProvider serviceProvider, IStreamGeneratorConfig generatorConfig)
        {
            var config = (generatorConfig as TimeBoundEventGeneratorConfig) ?? throw new ArgumentException("Invalid configuration type", nameof(generatorConfig));
            _startTime = config.StartTime;
            _endTime = config.EndTime;
            for (var i = 0; i < config.NumberOfStreams; i++)
            {
                _streamIds.Add(StreamId.Create(config.StreamNamespace, Guid.NewGuid()));
            }
            _payload = Activator.CreateInstance(config.PayloadType);
        }

        public bool TryReadEvents(DateTime utcNow, int maxCount, out List<IBatchContainer> events)
        {
            if (utcNow < _startTime || utcNow > _endTime)
            {
                events = null;
                return false;
            }

            events = new List<IBatchContainer>(maxCount);
            for (int i = 0; i < maxCount; i++)
            {
                var streamId = _streamIds[_sequenceId % _streamIds.Count];
                var container = new GeneratedBatchContainer(streamId, _payload, new EventSequenceTokenV2(_sequenceId));
                events.Add(container);
                _sequenceId++;
            }
            return true;
        }
    }
}

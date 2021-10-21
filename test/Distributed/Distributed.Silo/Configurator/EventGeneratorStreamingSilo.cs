using System;
using System.Collections.Generic;
using System.CommandLine;
using Distributed.GrainInterfaces;
using Distributed.GrainInterfaces.Streaming;
using Distributed.Grains.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Streams;

namespace Distributed.Silo.Configurator
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
                StreamNamespace = Constants.StreamingNamespace,
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
                .ConfigureServices(services => services.AddSingletonNamedService<IStreamGeneratorConfig>(Constants.StreamingProvider, (s, n) => generatorOptions))
                .AddPersistentStreams(
                    Constants.StreamingProvider,
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
}

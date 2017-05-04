
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Streams;
using TestGrainInterfaces;
using UnitTests.Grains;

namespace TestGrains
{
    [ImplicitStreamSubscription(StreamNamespace)]
    public class GeneratedEventCollectorGrain : Grain, IGeneratedEventCollectorGrain
    {
        public const string StreamNamespace = "Generated";

        private Logger logger;
        private IAsyncStream<GeneratedEvent> stream;
        private int counter;

        public override async Task OnActivateAsync()
        {
            logger = base.GetLogger("GeneratedEvenCollectorGrain " + base.IdentityString);
            logger.Info("OnActivateAsync");

            var streamProvider = GetStreamProvider(GeneratedStreamTestConstants.StreamProviderName);
            stream = streamProvider.GetStream<GeneratedEvent>(this.GetPrimaryKey(), StreamNamespace);

            await stream.SubscribeAsync(
                (e, t) =>
                {
                    counter++;
                    logger.Info("Received a generated event {0}, of {1} events", e, counter);
                    if (e.EventType == GeneratedEvent.GeneratedEventType.Fill)
                    {
                        return Task.CompletedTask;
                    }
                    var reporter = this.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);
                    return reporter.ReportResult(this.GetPrimaryKey(), GeneratedStreamTestConstants.StreamProviderName, StreamNamespace, counter);
                });
        }
    }
}

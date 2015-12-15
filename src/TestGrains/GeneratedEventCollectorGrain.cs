using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using TestGrainInterfaces;

namespace TestGrains
{
    [ImplicitStreamSubscription(StreamNamespace)]
    public class GeneratedEventCollectorGrain : Grain, IGeneratedEventCollectorGrain
    {
        public static Guid ReporterId = new Guid("f83247af-c14d-422c-8141-74d7a79717dc");
        public const string StreamNamespace = "Generated";
        public const string StreamProviderName = "GeneratedStreamProvider";

        private Logger logger;
        private IAsyncStream<GeneratedEvent> stream;
        private int counter;

        public override async Task OnActivateAsync()
        {
            logger = base.GetLogger("GeneratedEvenCollectorGrain " + base.IdentityString);
            logger.Info("OnActivateAsync");

            var streamProvider = GetStreamProvider(StreamProviderName);
            stream = streamProvider.GetStream<GeneratedEvent>(this.GetPrimaryKey(), StreamNamespace);

            await stream.SubscribeAsync(
                (e, t) =>
                {
                    counter++;
                    logger.Info("Received a generated event {0}, of {1} events", e, counter);
                    if (e.EventType == GeneratedEvent.GeneratedEventType.Fill)
                    {
                        return TaskDone.Done;
                    }
                    var reporter = this.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(ReporterId);
                    return reporter.ReportResult(this.GetPrimaryKey(), StreamProviderName, StreamNamespace, counter);
                });
        }
    }
}

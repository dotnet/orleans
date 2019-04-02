
using System.Collections.Generic;
using System.Linq;
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
        private int accumulated;

        public override async Task OnActivateAsync()
        {
            logger = this.GetLogger("GeneratedEvenCollectorGrain " + base.IdentityString);
            logger.Info("OnActivateAsync");

            var streamProvider = GetStreamProvider(GeneratedStreamTestConstants.StreamProviderName);
            stream = streamProvider.GetStream<GeneratedEvent>(this.GetPrimaryKey(), StreamNamespace);

            IList<StreamSubscriptionHandle<GeneratedEvent>> handles = await stream.GetAllSubscriptionHandles();
            if (handles.Count == 0)
            {
                await stream.SubscribeAsync(OnNextAsync);
            }
            else
            {
                foreach (StreamSubscriptionHandle<GeneratedEvent> handle in handles)
                {
                    await handle.ResumeAsync(OnNextAsync);
                }
            }
        }

        public Task OnNextAsync(IList<SequentialItem<GeneratedEvent>> items)
        {
            this.accumulated += items.Count;
            logger.Info("Received {Count} generated event.  Accumulated {Accumulated} events so far.", items.Count, this.accumulated);
            if (items.Last().Item.EventType == GeneratedEvent.GeneratedEventType.Fill)
            {
                return Task.CompletedTask;
            }
            var reporter = this.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);
            return reporter.ReportResult(this.GetPrimaryKey(), GeneratedStreamTestConstants.StreamProviderName, StreamNamespace, this.accumulated);
        }
    }
}

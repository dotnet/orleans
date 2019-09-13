
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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

        private ILogger logger;
        private IAsyncStream<GeneratedEvent> stream;
        private int accumulated;

        public GeneratedEventCollectorGrain(ILogger<GeneratedEventCollectorGrain> logger)
        {
            this.logger = logger;
        }

        public override async Task OnActivateAsync()
        {
            logger.Info($"{this.IdentityString} - OnActivateAsync");

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
            var last = items.Last();
            this.logger.Info("{Identity} - Received {Count} generated event with last token {Token}.  Accumulated {Accumulated} events so far.", this.IdentityString, items.Count, last.Token, this.accumulated);
            if (last.Item.EventType == GeneratedEvent.GeneratedEventType.Fill)
            {
                return Task.CompletedTask;
            }
            this.logger.Info("{Identity} - Received report event.  Total acccumulated: {Accumulated}.", this.IdentityString, this.accumulated);
            var reporter = this.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);
            return reporter.ReportResult(this.GetPrimaryKey(), GeneratedStreamTestConstants.StreamProviderName, StreamNamespace, this.accumulated);
        }
    }
}

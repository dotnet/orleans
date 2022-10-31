
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    [RegexImplicitStreamSubscription("THIS.WONT.MATCH.ONLY.FOR.TESTING.SERIALIZATION")]
    [ImplicitStreamSubscription(StreamNamespace)]
    public class GeneratedEventCollectorGrain : Grain, IGeneratedEventCollectorGrain
    {
        public const string StreamNamespace = "Generated";

        private ILogger logger;
        private IAsyncStream<GeneratedEvent> stream;
        private int accumulated;

        public GeneratedEventCollectorGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("OnActivateAsync");

            var streamProvider = this.GetStreamProvider(GeneratedStreamTestConstants.StreamProviderName);
            stream = streamProvider.GetStream<GeneratedEvent>(StreamNamespace, this.GetPrimaryKey());

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
            logger.LogInformation("Received {Count} generated event. Accumulated {Accumulated} events so far.", items.Count, this.accumulated);
            if (items.Last().Item.EventType == GeneratedEvent.GeneratedEventType.Fill)
            {
                return Task.CompletedTask;
            }
            var reporter = this.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);
            return reporter.ReportResult(this.GetPrimaryKey(), GeneratedStreamTestConstants.StreamProviderName, StreamNamespace, this.accumulated);
        }
    }
}

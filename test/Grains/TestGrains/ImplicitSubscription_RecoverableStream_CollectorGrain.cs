
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Providers;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Streams;
using TestGrainInterfaces;
using UnitTests.Grains;

namespace TestGrains
{
    [ImplicitStreamSubscription(StreamNamespace)]
    [StorageProvider(ProviderName = StorageProviderName)]
    public class ImplicitSubscription_RecoverableStream_CollectorGrain : Grain<StreamCheckpoint<int>>, IGeneratedEventCollectorGrain
    {
        public const string StreamNamespace = "RecoverableStream";
        public const string StorageProviderName = "AzureStorage";
        
        // grain instance state
        private ILogger logger;
        private IAsyncStream<GeneratedEvent> stream;

        public ImplicitSubscription_RecoverableStream_CollectorGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.Info("OnActivateAsync");

            await ReadStateAsync();

            Guid streamGuid = this.GetPrimaryKey();
            if (State.StreamGuid != streamGuid)
            {
                State.StreamGuid = streamGuid;
                State.StreamNamespace = StreamNamespace;
                await WriteStateAsync();
            }

            var streamProvider = this.GetStreamProvider(GeneratedStreamTestConstants.StreamProviderName);
            stream = streamProvider.GetStream<GeneratedEvent>(State.StreamGuid, State.StreamNamespace);

            await stream.SubscribeAsync(OnNextAsync, OnErrorAsync, State.RecoveryToken);
        }

        private async Task OnNextAsync(GeneratedEvent evt, StreamSequenceToken sequenceToken)
        {

            // ignore duplicates
            if (State.IsDuplicate(sequenceToken))
            {
                logger.Info("Received duplicate event.  StreamGuid: {0}, SequenceToken: {1}", State.StreamGuid, sequenceToken);
                return;
            }

            logger.Info("Received event.  StreamGuid: {0}, SequenceToken: {1}", State.StreamGuid, sequenceToken);

            // We will only update the start token if this is the first event we're processed
            // In that case, we'll want to save the start token in case something goes wrong.
            if (State.TryUpdateStartToken(sequenceToken))
            {
                await WriteStateAsync();
            }

            State.Accumulator++;
            State.LastProcessedToken = sequenceToken;
            if (evt.EventType != GeneratedEvent.GeneratedEventType.Report)
            {
                // every 10 events, checkpoint our grain state
                if (State.Accumulator % 10 != 0) return;
                logger.Info("Checkpointing: StreamGuid: {0}, StreamNamespace: {1}, SequenceToken: {2}, Accumulator: {3}.", State.StreamGuid, State.StreamNamespace, sequenceToken, State.Accumulator);
                await WriteStateAsync();
                return;
            }
            logger.Info("Final checkpointing: StreamGuid: {0}, StreamNamespace: {1}, SequenceToken: {2}, Accumulator: {3}.", State.StreamGuid, State.StreamNamespace, sequenceToken, State.Accumulator);
            await WriteStateAsync();
            var reporter = GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);
            await reporter.ReportResult(this.GetPrimaryKey(), GeneratedStreamTestConstants.StreamProviderName, StreamNamespace, State.Accumulator);
        }

        private Task OnErrorAsync(Exception ex)
        {
            logger.Info("Received an error on stream. StreamGuid: {0}, StreamNamespace: {1}, Exception: {2}.", State.StreamGuid, State.StreamNamespace, ex);
            return Task.CompletedTask;
        }
    }
}

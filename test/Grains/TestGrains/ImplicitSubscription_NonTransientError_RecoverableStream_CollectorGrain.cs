
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Placement;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Streams;
using TestGrainInterfaces;
using UnitTests.Grains;

namespace TestGrains
{
    [ImplicitStreamSubscription(StreamNamespace)]
    [PreferLocalPlacement]
    public class ImplicitSubscription_NonTransientError_RecoverableStream_CollectorGrain : Grain<StreamCheckpoint<int>>, IGeneratedEventCollectorGrain
    {
        public const string StreamNamespace = "NonTransientError_RecoverableStream";
     
        // grain instance state
        private ILogger logger;
        private IAsyncStream<GeneratedEvent> stream;

        private class FaultsState
        {
            public bool FaultCleared { get; set; }
        }
        private static readonly ConcurrentDictionary<Guid, FaultsState> FaultInjectionTracker = new ConcurrentDictionary<Guid, FaultsState>();
        private FaultsState myFaults;
        private FaultsState Faults { get { return myFaults ?? (myFaults = FaultInjectionTracker.GetOrAdd(this.GetPrimaryKey(), key => new FaultsState())); } }

        public ImplicitSubscription_NonTransientError_RecoverableStream_CollectorGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger("RecoverableStreamCollectorGrain " + base.IdentityString);
        }

        public override async Task OnActivateAsync()
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
            // Ignore duplicates
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

            // fault on 33rd event until fault is cleared
            if (State.Accumulator == 32 && !Faults.FaultCleared)
            {
                InjectFault();
            }

            State.Accumulator++;
            State.LastProcessedToken = sequenceToken;
            if (evt.EventType != GeneratedEvent.GeneratedEventType.Report)
            {
                // every 10 events, checkpoint our grain state
                if (State.Accumulator%10 != 0) return;
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
            Faults.FaultCleared = true;
            return Task.CompletedTask;
        }

        private void InjectFault()
        {
            logger.Info("InjectingFault: StreamGuid: {0}, StreamNamespace: {1}, SequenceToken: {2}, Accumulator: {3}.", State.StreamGuid, State.StreamNamespace, State.RecoveryToken, State.Accumulator);
            throw new ApplicationException("Injecting Fault");
        }
    }
}


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
    public class ImplicitSubscription_TransientError_RecoverableStream_CollectorGrain : Grain<StreamCheckpoint<int>>, IGeneratedEventCollectorGrain
    {
        public const string StreamNamespace = "TransientError_RecoverableStream";

        // Fault injection
        // Simulate simple transient failures.
        // Each failure occures once.
        // We place failures at key points to test recoverabilty.
        // - On first grain activation.
        // - after activation, but before storing start token.
        // - after start token has been stored, but befor it's message has been processed.
        // - mid stream processing (33rd message in this test, but realy depends on stream)
        // - last message in stream.
        private class FireOnNthTry
        {
            private readonly int attemptToFireOn;
            private int tries;

            public FireOnNthTry(int attemptToFireOn)
            {
                this.attemptToFireOn = attemptToFireOn;
            }

            public bool TryFire(Action fireAction)
            {
                tries++;
                if (tries != attemptToFireOn) return false;
                fireAction();
                return true;
            }
        }

        private class FaultsState
        {
            public readonly FireOnNthTry onActivateFault;
            public readonly FireOnNthTry onFirstMessageFault;
            public readonly FireOnNthTry onFirstMessageProcessedFault;
            public readonly FireOnNthTry on33rdMessageFault;
            public readonly FireOnNthTry onLastMessageFault;

            public FaultsState()
            {
                onActivateFault = new FireOnNthTry(1);
                onFirstMessageFault = new FireOnNthTry(1);
                onFirstMessageProcessedFault = new FireOnNthTry(1);
                on33rdMessageFault = new FireOnNthTry(33);
                onLastMessageFault = new FireOnNthTry(1);
            }
        }

        private static readonly ConcurrentDictionary<Guid, FaultsState> FaultInjectionTracker = new ConcurrentDictionary<Guid, FaultsState>();

        private FaultsState myFaults;
        private FaultsState Faults { get { return myFaults ?? (myFaults = FaultInjectionTracker.GetOrAdd(this.GetPrimaryKey(), key => new FaultsState())); } }
     
        // grain instance state
        private ILogger logger;
        private IAsyncStream<GeneratedEvent> stream;

        public ImplicitSubscription_TransientError_RecoverableStream_CollectorGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override async Task OnActivateAsync()
        {
            logger.Info("OnActivateAsync");

            Faults.onActivateFault.TryFire(InjectFault);
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
            foreach (StreamSubscriptionHandle<GeneratedEvent> handle in await stream.GetAllSubscriptionHandles())
            {
                await handle.ResumeAsync(OnNextAsync, OnErrorAsync, State.RecoveryToken);
            }
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
                Faults.onFirstMessageFault.TryFire(InjectFault);
                await WriteStateAsync();
            }

            State.Accumulator++;
            State.LastProcessedToken = sequenceToken;
            if (evt.EventType != GeneratedEvent.GeneratedEventType.Report)
            {
                Faults.onFirstMessageProcessedFault.TryFire(InjectFault);
                Faults.on33rdMessageFault.TryFire(InjectFault);
                // every 10 events, checkpoint our grain state
                if (State.Accumulator%10 != 0) return;
                logger.Info("Checkpointing: StreamGuid: {0}, StreamNamespace: {1}, SequenceToken: {2}, Accumulator: {3}.", State.StreamGuid, State.StreamNamespace, sequenceToken, State.Accumulator);
                await WriteStateAsync();
                return;
            }
            Faults.onLastMessageFault.TryFire(InjectFault);
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

        private void InjectFault()
        {
            logger.Info("InjectingFault: StreamGuid: {0}, StreamNamespace: {1}, SequenceToken: {2}, Accumulator: {3}.", State.StreamGuid, State.StreamNamespace, State.RecoveryToken, State.Accumulator);
            DeactivateOnIdle(); // kill grain and reaload from checkpoint
            throw new ApplicationException("Injecting Fault");
        }
    }
}

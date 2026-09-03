using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Orleans.Configuration;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Runs transaction tests which inject faults at controlled protocol phases and verify recovery.
    /// </summary>
    public class ControlledFaultInjectionTransactionTestRunner : TransactionTestRunnerBase
    {
        private static readonly TimeSpan RecoveryWatchdog =
            new ClientMessagingOptions().ResponseTimeout
            + TransactionalStateOptions.DefaultPrepareTimeout
            + TransactionalStateOptions.DefaultRemoteTransactionPingFrequency
            + TransactionalStateOptions.DefaultLockTimeout;

        /// <summary>
        /// Initializes a new instance of the <see cref="ControlledFaultInjectionTransactionTestRunner"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to access test grains.</param>
        /// <param name="output">The callback used to write test output.</param>
        public ControlledFaultInjectionTransactionTestRunner(IGrainFactory grainFactory, Action<string> output)
         : base(grainFactory, output)
        { }

        /// <summary>
        /// Verifies that a committed value remains readable after the grain is deactivated and reactivated.
        /// </summary>
        /// <returns>A task which represents the test.</returns>
        public virtual async Task SingleGrainReadTransaction()
        {
            const int expected = 5;

            IFaultInjectionTransactionTestGrain grain = grainFactory.GetGrain<IFaultInjectionTransactionTestGrain>(Guid.NewGuid());
            await grain.Set(expected);
            int actual = await grain.Get();
            actual.Should().Be(expected);
            await grain.Deactivate();
            actual = await grain.Get();
            actual.Should().Be(expected);
        }
        
        /// <summary>
        /// Verifies that a transactional write remains committed after the grain is deactivated and reactivated.
        /// </summary>
        /// <returns>A task which represents the test.</returns>
        public virtual async Task SingleGrainWriteTransaction()
        {
            const int delta = 5;
            IFaultInjectionTransactionTestGrain grain = this.grainFactory.GetGrain<IFaultInjectionTransactionTestGrain>(Guid.NewGuid());
            int original = await grain.Get();
            await grain.Add(delta);
            await grain.Deactivate();
            int expected = original + delta;
            int actual = await grain.Get();
            actual.Should().Be(expected);
        }

        /// <summary>
        /// Injects a fault into a multi-grain write at a selected protocol phase and verifies the recovered state.
        /// </summary>
        /// <param name="injectionPhase">The transaction protocol phase at which to inject the fault.</param>
        /// <param name="injectionType">The type of fault to inject.</param>
        /// <returns>A task which represents the fault-injection and recovery test.</returns>
        public virtual async Task MultiGrainWriteTransaction_FaultInjection(TransactionFaultInjectPhase injectionPhase, FaultInjectionType injectionType)
        {
            const int setval = 5;
            const int addval = 7;
            int? expected = setval + addval;
            const int grainCount = TransactionTestConstants.MaxCoordinatedTransactions;
            var faultInjectionControl = new FaultInjectionControl() { FaultInjectionPhase = injectionPhase, FaultInjectionType = injectionType };
            List<IFaultInjectionTransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => this.grainFactory.GetGrain<IFaultInjectionTransactionTestGrain>(Guid.NewGuid()))
                    .ToList();

            IFaultInjectionTransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<IFaultInjectionTransactionCoordinatorGrain>(Guid.NewGuid());

            var grainIds = grains.Select(grain => grain.GetGrainId()).ToHashSet();
            using var recoveryEvents = new TransactionRecoveryEventObserver(grainIds);
            var faultObserved = new TaskCompletionSource<FaultInjectionEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var faultSubscription = FaultInjectionDiagnosticEvents.Subscribe(evt =>
            {
                if (grainIds.Contains(evt.GrainId)
                    && evt.Phase == injectionPhase
                    && evt.Type == injectionType)
                {
                    faultObserved.TrySetResult(evt);
                }
            });
            await this.ExecuteAndWaitForCommit(
                () => coordinator.MultiGrainSet(grains, setval),
                grains.Count,
                recoveryEvents,
                GetDeadline());
            var faultAttemptSequence = recoveryEvents.LatestRelevantSequence;
            var hasStorageFault = injectionType is FaultInjectionType.ExceptionBeforeStore
                or FaultInjectionType.ExceptionAfterStore
                or FaultInjectionType.GenericExceptionAfterStore;
            var waitForRecovery = false;
            try
            {
                await this.ExecuteAndWaitForCommit(
                    () => coordinator.MultiGrainAddAndFaultInjection(grains, addval, faultInjectionControl),
                    grains.Count,
                    recoveryEvents,
                    GetDeadline());
            }
            catch (OrleansTransactionAbortedException exception)
            {
                this.testOutput($"Fault-injected transaction aborted: {exception}");
                expected = setval;
                waitForRecovery = hasStorageFault;
            }
            catch (OrleansTransactionException exception)
            {
                this.testOutput($"Fault-injected transaction failed with an ambiguous outcome: {exception}");
                expected = null;
                waitForRecovery = hasStorageFault;
            }

            var fault = await this.ObserveFaultInjection(
                faultObserved.Task,
                injectionPhase,
                injectionType,
                recoveryEvents,
                GetDeadline());
            if (waitForRecovery)
            {
                var recovery = await recoveryEvents.WaitForRecoveryCompletionAsync(
                    fault.TransactionId,
                    fault.GrainId,
                    faultAttemptSequence,
                    GetDeadline());
                this.testOutput(
                    $"Fault-injected transaction recovery completed. "
                    + TransactionRecoveryEventObserver.FormatTransition(recovery).Trim());
            }

            var actualValues = await this.ReadAfterRecovery(grains, recoveryEvents, GetDeadline());
            actualValues.Should().OnlyContain(value => value == actualValues[0]);
            if (expected is { } expectedValue)
            {
                actualValues.Should().OnlyContain(value => value == expectedValue);
            }
            else
            {
                actualValues.Should().OnlyContain(value => value == setval || value == setval + addval);
            }
        }

        private async Task ExecuteAndWaitForCommit(
            Func<Task> transaction,
            int participantCount,
            TransactionRecoveryEventObserver recoveryEvents,
            long deadline)
        {
            var sequence = recoveryEvents.LatestRelevantSequence;
            await transaction();
            var commit = await recoveryEvents.WaitForCommitConfirmationAsync(sequence, participantCount, deadline);
            this.testOutput(
                $"Transaction commit and participant confirmations completed. "
                + TransactionRecoveryEventObserver.FormatTransition(commit).Trim());
        }

        private async Task<int[]> ReadAfterRecovery(
            List<IFaultInjectionTransactionTestGrain> grains,
            TransactionRecoveryEventObserver recoveryEvents,
            long deadline)
        {
            var attempt = 0;
            while (Stopwatch.GetTimestamp() < deadline)
            {
                attempt++;
                var sequence = recoveryEvents.LatestRelevantSequence;
                try
                {
                    return await Task.WhenAll(grains.Select(grain => grain.Get()));
                }
                catch (Exception exception) when (exception is OrleansTransactionTransientFailureException
                    or OrleansTransactionInDoubtException
                    or TimeoutException)
                {
                    this.testOutput(
                        $"Recovery read {attempt} failed with {exception.GetType().Name}; "
                        + "waiting for transaction recovery progress.");
                    var transition = await recoveryEvents.WaitForNextTransitionAsync(sequence, deadline);
                    this.testOutput(
                        $"Recovery read {attempt} observed progress. "
                        + TransactionRecoveryEventObserver.FormatTransition(transition).Trim());
                }
            }

            throw new TimeoutException(
                $"The fault-injected transaction did not become readable within the protocol-derived "
                + $"{RecoveryWatchdog} watchdog."
                + Environment.NewLine
                + recoveryEvents.FormatTimeline());
        }

        private async Task<FaultInjectionEvent> ObserveFaultInjection(
            Task<FaultInjectionEvent> observation,
            TransactionFaultInjectPhase phase,
            FaultInjectionType type,
            TransactionRecoveryEventObserver recoveryEvents,
            long deadline)
        {
            try
            {
                if (!observation.IsCompleted)
                {
                    var now = Stopwatch.GetTimestamp();
                    if (now >= deadline)
                    {
                        throw new TimeoutException();
                    }

                    return await observation.WaitAsync(Stopwatch.GetElapsedTime(now, deadline));
                }

                return await observation;
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"The configured {type} fault at {phase} was not observed before the "
                    + $"{RecoveryWatchdog} watchdog expired."
                    + Environment.NewLine
                    + recoveryEvents.FormatTimeline());
            }
        }

        private static long GetDeadline()
            => Stopwatch.GetTimestamp() + (long)(RecoveryWatchdog.TotalSeconds * Stopwatch.Frequency);
    }
}

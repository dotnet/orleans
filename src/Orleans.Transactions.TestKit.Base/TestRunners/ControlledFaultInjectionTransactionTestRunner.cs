using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.Transactions.TestKit
{
    public class ControlledFaultInjectionTransactionTestRunner : TransactionTestRunnerBase
    {
        private static readonly TimeSpan RecoveryWatchdog =
            new ClientMessagingOptions().ResponseTimeout
            + TransactionalStateOptions.DefaultPrepareTimeout
            + TransactionalStateOptions.DefaultRemoteTransactionPingFrequency
            + TransactionalStateOptions.DefaultLockTimeout;

        public ControlledFaultInjectionTransactionTestRunner(IGrainFactory grainFactory, Action<string> output)
         : base(grainFactory, output)
        { }
        
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

        public virtual async Task MultiGrainWriteTransaction_FaultInjection(TransactionFaultInjectPhase injectionPhase, FaultInjectionType injectionType)
        {
            const int setval = 5;
            const int addval = 7;
            int expected = setval + addval;
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
                var deadline = GetDeadline();
                var fault = await this.ObserveFaultInjection(
                    faultObserved.Task,
                    injectionPhase,
                    injectionType,
                    recoveryEvents,
                    deadline);
                await this.WaitForRecoveryCompletion(
                    fault.TransactionId,
                    fault.GrainId,
                    faultAttemptSequence,
                    recoveryEvents,
                    deadline);
                await this.RetryAfterRecovery(
                    () => this.ExecuteAndWaitForCommit(
                        () => coordinator.MultiGrainAddAndFaultInjection(grains, addval),
                        grains.Count,
                        recoveryEvents,
                        deadline),
                    recoveryEvents,
                    deadline);
            }
            catch (OrleansTransactionException exception)
            {
                this.testOutput($"Fault-injected transaction failed with an ambiguous outcome: {exception}");
                var deadline = GetDeadline();
                var fault = await this.ObserveFaultInjection(
                    faultObserved.Task,
                    injectionPhase,
                    injectionType,
                    recoveryEvents,
                    deadline);
                await this.WaitForRecoveryCompletion(
                    fault.TransactionId,
                    fault.GrainId,
                    faultAttemptSequence,
                    recoveryEvents,
                    deadline);
                expected = await this.RetryAfterRecovery(
                    async () =>
                    {
                        var result = await grains[0].Get() + addval;
                        await this.ExecuteAndWaitForCommit(
                            () => coordinator.MultiGrainAddAndFaultInjection(grains, addval),
                            grains.Count,
                            recoveryEvents,
                            deadline);
                        return result;
                    },
                    recoveryEvents,
                    deadline);
            }

            await this.ObserveFaultInjection(
                faultObserved.Task,
                injectionPhase,
                injectionType,
                recoveryEvents,
                GetDeadline());

            //if transactional state loaded correctly after reactivation, then following should pass
            foreach (var grain in grains)
            {
                int actual = await grain.Get();
                actual.Should().Be(expected);
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

        private async Task RetryAfterRecovery(
            Func<Task> transaction,
            TransactionRecoveryEventObserver recoveryEvents,
            long deadline)
            => await this.RetryAfterRecovery(
                async () =>
                {
                    await transaction();
                    return true;
                },
                recoveryEvents,
                deadline);

        private async Task<TResult> RetryAfterRecovery<TResult>(
            Func<Task<TResult>> transaction,
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
                    return await transaction();
                }
                catch (OrleansCascadingAbortException exception)
                {
                    this.testOutput(
                        $"Recovery retry {attempt} observed a cascading abort for transaction "
                        + $"{exception.TransactionId}; waiting for transaction recovery progress.");
                    var transition = await recoveryEvents.WaitForNextTransitionAsync(sequence, deadline);
                    this.testOutput(
                        $"Recovery retry {attempt} observed progress. "
                        + TransactionRecoveryEventObserver.FormatTransition(transition).Trim());
                }
            }

            throw new TimeoutException(
                $"The fault-injected transaction did not recover within the protocol-derived {RecoveryWatchdog} watchdog."
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

        private async Task WaitForRecoveryCompletion(
            Guid transactionId,
            GrainId faultingGrainId,
            long afterSequence,
            TransactionRecoveryEventObserver recoveryEvents,
            long deadline)
        {
            var transition = await recoveryEvents.WaitForRecoveryCompletionAsync(
                transactionId,
                faultingGrainId,
                afterSequence,
                deadline);
            this.testOutput(
                $"Fault-injected transaction recovery completed. "
                + TransactionRecoveryEventObserver.FormatTransition(transition).Trim());
        }

        private static long GetDeadline()
            => Stopwatch.GetTimestamp() + (long)(RecoveryWatchdog.TotalSeconds * Stopwatch.Frequency);
    }
}

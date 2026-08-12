using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.Transactions.TestKit.Correctnesss;

namespace Orleans.Transactions.TestKit
{
    public partial class TransactionRecoveryTestsRunner : TransactionTestRunnerBase
    {
        private static readonly TimeSpan FailureDetectionSchedulingMargin = TimeSpan.FromSeconds(15);

        private readonly TestCluster testCluster;
        private readonly ILogger logger;
        private readonly TimeSpan clientResponseTimeout;
        private readonly TimeSpan failureDetectionTimeout;
        private readonly TimeSpan recoveryTimeout;

        protected void Log(string message)
        {
            this.testOutput($"[{DateTime.Now}] {message}");
            LogInformationMessage(this.logger, message);
        }

        private class ExpectedGrainActivity
        {
            public ExpectedGrainActivity(Guid grainId, ITransactionalBitArrayGrain grain)
            {
                this.GrainId = grainId;
                this.Grain = grain;
            }
            public Guid GrainId { get; }
            public ITransactionalBitArrayGrain Grain { get; }
            public GrainId RuntimeGrainId => this.Grain.GetGrainId();
            public BitArrayState Expected { get; } = new BitArrayState();
            public BitArrayState Unambiguous { get; } = new BitArrayState();
            public List<BitArrayState> Actual { get; set; } = null!;
            public async Task GetActual()
            {
                try
                {
                    this.Actual = await this.Grain.Get();
                } catch(Exception)
                {
                    // allow a single retry
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    this.Actual = await this.Grain.Get();
                }
            }
        }

        private sealed record TransactionFailure(int Index, Guid[] GrainIds, Exception Exception, long ObservedAt);

        private sealed record InFlightBatch(int Index, int PendingCount);

        private sealed record RecoveryResult(
            bool Succeeded,
            bool LastProbeSucceeded,
            int Attempts,
            int RemainingGroupCount,
            int LastTransactionIndex,
            TimeSpan Elapsed);

        public TransactionRecoveryTestsRunner(TestCluster testCluster, Action<string> testOutput)
            : base(testCluster.GrainFactory!, testOutput) // Transaction test clusters initialize a client.
        {
            this.testCluster = testCluster;
            this.logger = this.testCluster.ServiceProvider.GetService<ILogger<TransactionRecoveryTestsRunner>>()!;
            this.clientResponseTimeout = this.testCluster.ServiceProvider
                .GetRequiredService<IOptions<ClientMessagingOptions>>()
                .Value
                .ResponseTimeout;
            this.failureDetectionTimeout = TransactionRecoveryFailureObservation.GetTimeouts(
                gracefulShutdown: true,
                this.clientResponseTimeout,
                FailureDetectionSchedulingMargin).MaximumDuration;
            this.recoveryTimeout =
                this.failureDetectionTimeout + TransactionalStateOptions.DefaultRemoteTransactionPingFrequency;
        }

        public virtual Task TransactionWillRecoverAfterRandomSiloGracefulShutdown(string transactionTestGrainClassName, int concurrent)
        {
            return TransactionWillRecoverAfterRandomSiloFailure(transactionTestGrainClassName, concurrent, true);
        }

        public virtual Task TransactionWillRecoverAfterRandomSiloUnGracefulShutdown(string transactionTestGrainClassName, int concurrent)
        {
            return TransactionWillRecoverAfterRandomSiloFailure(transactionTestGrainClassName, concurrent, false);
        }

        /// <summary>
        /// Verifies recovery when the transaction manager activation is terminated while waiting for remote prepares.
        /// </summary>
        public Task TransactionWillRecoverAfterManagerWait(string transactionTestGrainClassName)
            => TransactionWillRecoverAfterTargetedPhase(
                transactionTestGrainClassName,
                TransactionRecoveryEventObserver.RecoveryTransitionKind.TransactionManagerWaitingForPrepared,
                requireParticipantConfirmation: false);

        /// <summary>
        /// Verifies recovery when a remote participant activation is terminated after persisting its prepare.
        /// </summary>
        public Task TransactionWillRecoverAfterRemotePreparePersisted(string transactionTestGrainClassName)
            => TransactionWillRecoverAfterTargetedPhase(
                transactionTestGrainClassName,
                TransactionRecoveryEventObserver.RecoveryTransitionKind.RemotePreparePersisted,
                requireParticipantConfirmation: false);

        /// <summary>
        /// Verifies recovery when the transaction manager activation is terminated after its commit is durable.
        /// </summary>
        public Task TransactionWillRecoverAfterLocalCommitStored(string transactionTestGrainClassName)
            => TransactionWillRecoverAfterTargetedPhase(
                transactionTestGrainClassName,
                TransactionRecoveryEventObserver.RecoveryTransitionKind.StorageWriteCompleted,
                requireParticipantConfirmation: true);

        private async Task TransactionWillRecoverAfterTargetedPhase(
            string transactionTestGrainClassName,
            TransactionRecoveryEventObserver.RecoveryTransitionKind phase,
            bool requireParticipantConfirmation)
        {
            var index = 0;
            int getIndex() => Interlocked.Increment(ref index) - 1;
            var txGrains = Enumerable.Range(0, 2)
                .Select(_ => Guid.NewGuid())
                .Select(grainId => new ExpectedGrainActivity(
                    grainId,
                    TestGrain<ITransactionalBitArrayGrain>(transactionTestGrainClassName, grainId)))
                .ToList();
            var transactionGroups = new[] { txGrains };

            await WakeupGrains(txGrains.Select(grain => grain.Grain).ToList());
            (await AllTxSucceed(transactionGroups, getIndex())).Should().BeTrue();
            await ValidateResults(txGrains, transactionGroups);

            using var recoveryEvents = new TransactionRecoveryEventObserver(
                txGrains.Select(grain => grain.RuntimeGrainId));
            using var phaseGate = recoveryEvents.GateNextTransition(transition =>
                transition.Kind == phase
                && (!requireParticipantConfirmation
                    || transition.CommitCount > 0 && !transition.TransactionIds.IsDefaultOrEmpty));
            var phaseDeadline = Stopwatch.GetTimestamp()
                + (long)(this.failureDetectionTimeout.TotalSeconds * Stopwatch.Frequency);
            var attemptIndex = getIndex();
            var attempt = RunAllTxReportFailed(transactionGroups, attemptIndex);
            TransactionRecoveryEventObserver.RecoveryTransition transition;
            try
            {
                transition = await phaseGate.WaitAsync(phaseDeadline);
            }
            catch
            {
                attempt.Ignore();
                throw;
            }

            if (transition.SiloAddress is null || transition.ActivationId.IsDefault)
            {
                attempt.Ignore();
                throw new InvalidOperationException(
                    $"The {phase} transition did not identify its owning silo and activation."
                    + Environment.NewLine
                    + recoveryEvents.FormatTimeline());
            }

            this.Log(
                $"Recovery phase=targeted-gate reached, timestamp={DateTime.UtcNow:O}. "
                + TransactionRecoveryEventObserver.FormatTransition(transition).Trim());
            recoveryEvents.SetRelevantGrains(txGrains.Select(grain => grain.RuntimeGrainId));

            TransactionRecoveryEventObserver.PhaseGate? cleanupGate = null;
            Task<TransactionRecoveryEventObserver.RecoveryTransition>? cleanupObservation = null;
            if (requireParticipantConfirmation)
            {
                cleanupGate = recoveryEvents.GateNextTransition(candidate =>
                    candidate.Sequence > transition.Sequence
                    && candidate.Kind == TransactionRecoveryEventObserver.RecoveryTransitionKind.TransactionConfirmCompleted
                    && candidate.TransactionId is { } transactionId
                    && transition.TransactionIds.Contains(transactionId)
                    && candidate.GrainId != transition.GrainId);
                cleanupObservation = ObserveAndReleaseGateAsync(cleanupGate, GetDeadline(this.recoveryTimeout));
            }

            try
            {
                var siloToTerminate = this.testCluster.Silos.Single(
                    silo => silo.SiloAddress.Equals(transition.SiloAddress));
                var applicationLifetime = this.testCluster
                    .GetSiloServiceProvider(siloToTerminate.SiloAddress)
                    .GetRequiredService<IHostApplicationLifetime>();
                var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var stoppingRegistration = applicationLifetime.ApplicationStopping.Register(
                    () => stopping.TrySetResult());
                var shutdown = this.testCluster.KillSiloAsync(siloToTerminate);
                try
                {
                    await stopping.Task.WaitAsync(this.failureDetectionTimeout);
                    phaseGate.Release();
                    await shutdown.WaitAsync(this.recoveryTimeout);
                }
                catch (TimeoutException)
                {
                    phaseGate.Release();
                    shutdown.Ignore();
                    throw;
                }

                List<ExpectedGrainActivity>[]? failedGroups;
                try
                {
                    failedGroups = await attempt.WaitAsync(this.failureDetectionTimeout);
                }
                catch (TimeoutException)
                {
                    attempt.Ignore();
                    throw new TimeoutException(
                        $"The transaction gated at {phase} did not settle within {this.failureDetectionTimeout}.");
                }

                var groupsToProbe = failedGroups ?? transactionGroups;
                var liveness = this.testCluster.WaitForLivenessToStabilizeAsync(didKill: true);
                try
                {
                    await liveness.WaitAsync(this.recoveryTimeout);
                }
                catch (TimeoutException)
                {
                    liveness.Ignore();
                    throw;
                }
                var recovery = await RecoverTransactions(
                    groupsToProbe,
                    getIndex,
                    this.recoveryTimeout,
                    this.failureDetectionTimeout,
                    recoveryEvents);
                recovery.Succeeded.Should().BeTrue(
                    $"the transaction path gated at {phase} should recover within {this.recoveryTimeout}");

                if (cleanupObservation is not null)
                {
                    var cleanup = await cleanupObservation;
                    cleanup.ActivationId.IsDefault.Should().BeFalse();
                    cleanup.SiloAddress.Should().NotBeNull();
                }

                await ValidateResults(txGrains, transactionGroups);
            }
            finally
            {
                phaseGate.Release();
                cleanupObservation?.Ignore();
                cleanupGate?.Dispose();
            }
        }

        private static async Task<TransactionRecoveryEventObserver.RecoveryTransition> ObserveAndReleaseGateAsync(
            TransactionRecoveryEventObserver.PhaseGate gate,
            long deadline)
        {
            try
            {
                return await gate.WaitAsync(deadline);
            }
            finally
            {
                gate.Release();
            }
        }

        private static long GetDeadline(TimeSpan timeout)
            => Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        protected virtual async Task TransactionWillRecoverAfterRandomSiloFailure(string transactionTestGrainClassName, int concurrent, bool gracefulShutdown)
        {
            var index = 0;
            int getIndex() => Interlocked.Increment(ref index) - 1;
            List<ExpectedGrainActivity> txGrains = Enumerable.Range(0, concurrent * 2)
                .Select(i => Guid.NewGuid())
                .Select(grainId => new ExpectedGrainActivity(grainId, TestGrain<ITransactionalBitArrayGrain>(transactionTestGrainClassName, grainId)))
                .ToList();
            //ping all grains to activate them
            await WakeupGrains(txGrains.Select(g=>g.Grain).ToList());
            List<ExpectedGrainActivity>[] transactionGroups = txGrains
                .Select((txGrain, i) => new { index = i, value = txGrain })
                .GroupBy(v => v.index / 2)
                .Select(g => g.Select(i => i.value).ToList())
                .ToArray();
            using var recoveryEvents = new TransactionRecoveryEventObserver(txGrains.Select(grain => grain.RuntimeGrainId));
            var txSucceedBeforeInterruption = await AllTxSucceed(transactionGroups, getIndex());
            txSucceedBeforeInterruption.Should().BeTrue();
            await ValidateResults(txGrains, transactionGroups);

            // have transactions in flight when silo goes down
            using var stopProducing = new CancellationTokenSource();
            var firstFailure = new TaskCompletionSource<TransactionFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstInFlightBatch = new TaskCompletionSource<InFlightBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<List<ExpectedGrainActivity>[]?> producer = RunWhileSucceeding(
                transactionGroups,
                getIndex,
                stopProducing,
                firstFailure,
                firstInFlightBatch);
            var inFlightBatch = await firstInFlightBatch.Task.WaitAsync(this.failureDetectionTimeout);

            if (firstFailure.Task.IsCompleted)
            {
                stopProducing.Cancel();
                producer.Ignore();
                var prematureFailure = await firstFailure.Task;
                throw new InvalidOperationException(
                    $"A transaction failed before the silo was terminated. Index: {prematureFailure.Index}. "
                    + $"Groups: {string.Join(":", prematureFailure.GrainIds)}. Exception: {prematureFailure.Exception.GetType().Name}.");
            }

            var pendingAtShutdownRequest = inFlightBatch.PendingCount;
            pendingAtShutdownRequest.Should().BeGreaterThan(
                0,
                "the silo failure must overlap an incomplete mutating transaction batch");
            var siloToTerminate = this.testCluster.Silos[Random.Shared.Next(this.testCluster.Silos.Count)];
            var shutdownMode = gracefulShutdown ? "graceful-stop" : "in-process-kill-shutdown";
            this.Log(
                $"Recovery phase=silo-shutdown requested, timestamp={DateTime.UtcNow:O}. Silo={siloToTerminate.SiloAddress} "
                + $"({siloToTerminate.Name}), mode={shutdownMode}. "
                + $"inFlightIndex={inFlightBatch.Index}, pendingMutations={pendingAtShutdownRequest}. "
                + "The in-process kill mode requests host shutdown through cancellation; it does not terminate a process.");

            var shutdownStartedAt = Stopwatch.GetTimestamp();
            if (gracefulShutdown)
                await this.testCluster.StopSiloAsync(siloToTerminate);
            else
                await this.testCluster.KillSiloAsync(siloToTerminate);
            var shutdownElapsed = Stopwatch.GetElapsedTime(shutdownStartedAt);
            this.Log(
                $"Recovery phase=silo-shutdown completed, timestamp={DateTime.UtcNow:O}. Silo={siloToTerminate.SiloAddress}, "
                + $"mode={shutdownMode}, elapsed={shutdownElapsed}.");

            this.Log("Observing transaction activity after silo shutdown");
            var failureDetectionStartedAt = Stopwatch.GetTimestamp();
            var failureObservationTimeout = TransactionRecoveryFailureObservation.GetTimeouts(
                gracefulShutdown,
                this.clientResponseTimeout,
                FailureDetectionSchedulingMargin);
            this.Log(
                $"Recovery phase=failure-watchdog started. ObservationWindow="
                + $"{failureObservationTimeout.ObservationWindow}, clientResponseTimeout={this.clientResponseTimeout}, "
                + $"producerDrainTimeout={failureObservationTimeout.ProducerDrainTimeout}, "
                + $"schedulingMargin={FailureDetectionSchedulingMargin}, "
                + $"watchdog={failureObservationTimeout.MaximumDuration}.");
            var failureDetection = await TransactionRecoveryFailureObservation.DetectAsync(
                producer,
                firstFailure.Task,
                stopProducing,
                failureObservationTimeout.ObservationWindow,
                failureObservationTimeout.ProducerDrainTimeout);
            this.Log(
                $"Recovery phase=failure-watchdog completed, timestamp={DateTime.UtcNow:O}. "
                + $"Outcome={failureDetection.Kind}, elapsed={failureDetection.Elapsed}, "
                + $"drainElapsed={failureDetection.DrainElapsed}, producerSettled={failureDetection.ProducerSettled}.");

            if (failureDetection.Kind == TransactionRecoveryFailureObservation.OutcomeKind.AttemptTimedOut)
            {
                throw new TimeoutException(
                    $"The in-flight transaction attempt did not settle within the {failureObservationTimeout.MaximumDuration} "
                    + $"failure-detection deadline after silo death. Shutdown elapsed={shutdownElapsed}, "
                    + $"failure detection elapsed={failureDetection.Elapsed}, drain elapsed={failureDetection.DrainElapsed}. "
                    + $"Performed {Volatile.Read(ref index)} transactions on each group.");
            }

            List<ExpectedGrainActivity>[] groupsToRecover;
            if (failureDetection.Kind == TransactionRecoveryFailureObservation.OutcomeKind.StoppedWithoutFailure)
            {
                if (!gracefulShutdown)
                {
                    throw new InvalidOperationException(
                        $"Transaction production stopped without observing a failure after silo death. "
                        + $"Shutdown elapsed={shutdownElapsed}, failure detection elapsed={failureDetection.Elapsed}. "
                        + $"Performed {Volatile.Read(ref index)} transactions on each group.");
                }

                groupsToRecover = transactionGroups;
                recoveryEvents.SetRelevantGrains(txGrains.Select(grain => grain.RuntimeGrainId));
                this.Log(
                    $"Recovery phase=transaction-continuity observed, timestamp={DateTime.UtcNow:O}. "
                    + "The graceful silo shutdown produced no client-visible transaction failure; "
                    + $"all {groupsToRecover.Length} transaction groups will be probed after convergence.");
            }
            else
            {
                var interruption = failureDetection.Failure!;
                if (!failureDetection.ProducerSettled)
                {
                    throw new TimeoutException(
                        $"A transaction failure was observed at index {interruption.Index}, but the in-flight batch did not "
                        + $"settle within the {failureObservationTimeout.MaximumDuration} absolute deadline. No recovery probe was started "
                        + $"while that state-mutating attempt remained active. Drain elapsed={failureDetection.DrainElapsed}.");
                }

                var failedGroups = failureDetection.ProducerResult;
                if (TransactionRecoveryFailureObservation.IsPremature(interruption.ObservedAt, shutdownStartedAt))
                {
                    throw new InvalidOperationException(
                        $"A transaction failed before silo shutdown began. Index: {interruption.Index}. "
                        + $"Groups: {string.Join(":", interruption.GrainIds)}. Exception: {interruption.Exception.GetType().Name}.");
                }

                if (interruption.ObservedAt >= failureDetectionStartedAt
                    && Stopwatch.GetElapsedTime(failureDetectionStartedAt, interruption.ObservedAt) > failureObservationTimeout.MaximumDuration)
                {
                    throw new TimeoutException(
                        $"No transaction failure was observed within the {failureObservationTimeout.MaximumDuration} watchdog after silo death. "
                        + $"The first later failure was at index {interruption.Index} after "
                        + $"{Stopwatch.GetElapsedTime(failureDetectionStartedAt, interruption.ObservedAt)}.");
                }

                var firstFailureAfterShutdownRequest = Stopwatch.GetElapsedTime(shutdownStartedAt, interruption.ObservedAt);
                var firstFailureRelativeToShutdownCompletion = Stopwatch.GetElapsedTime(failureDetectionStartedAt, interruption.ObservedAt);
                this.Log(
                    $"Recovery phase=transaction-terminal-failure observed, timestamp={DateTime.UtcNow:O}. "
                    + $"Index={interruption.Index}, "
                    + $"grains={string.Join(":", interruption.GrainIds)}, "
                    + $"afterShutdownRequest={firstFailureAfterShutdownRequest}, "
                    + $"relativeToShutdownCompletion={firstFailureRelativeToShutdownCompletion}, "
                    + $"producerDrain={failureDetection.DrainElapsed}, "
                    + $"exception={interruption.Exception.GetType().Name}: {interruption.Exception.Message}.");

                failedGroups.Should().NotBeNullOrEmpty(
                    "the drained producer must identify the transaction groups affected by the observed failure");
                groupsToRecover = failedGroups!;
                recoveryEvents.SetRelevantGrains(groupsToRecover.SelectMany(group => group).Select(grain => grain.RuntimeGrainId));
            }

            var convergenceStartedAt = Stopwatch.GetTimestamp();
            this.Log(
                $"Recovery phase=membership-directory-convergence started, timestamp={DateTime.UtcNow:O}, "
                + $"groups={FormatGroups(groupsToRecover)}.");
            await this.testCluster.WaitForLivenessToStabilizeAsync(didKill: !gracefulShutdown);
            var convergenceElapsed = Stopwatch.GetElapsedTime(convergenceStartedAt);
            this.Log(
                $"Recovery phase=membership-directory-convergence completed, timestamp={DateTime.UtcNow:O}, "
                + $"elapsed={convergenceElapsed}, groups={FormatGroups(groupsToRecover)}.");

            this.Log($"Waiting for system to recover. Performed {Volatile.Read(ref index)} transactions on each group.");
            this.Log(
                $"Recovery phase=transaction-path-watchdog started. "
                + $"watchdog={this.recoveryTimeout}, failureDetection={this.failureDetectionTimeout}, "
                + $"remotePingFrequency={TransactionalStateOptions.DefaultRemoteTransactionPingFrequency}.");
            var recovery = await RecoverTransactions(
                groupsToRecover,
                getIndex,
                this.recoveryTimeout,
                this.failureDetectionTimeout,
                recoveryEvents);
            this.Log(
                $"Recovery phase=transaction-path-probe completed, timestamp={DateTime.UtcNow:O}. Succeeded={recovery.Succeeded}, "
                + $"lastProbeSucceeded={recovery.LastProbeSucceeded}, "
                + $"attempts={recovery.Attempts}, remainingGroups={recovery.RemainingGroupCount}, "
                + $"lastIndex={recovery.LastTransactionIndex}, elapsed={recovery.Elapsed}. "
                + $"Performed {Volatile.Read(ref index)} transactions on each group.");
            recovery.Succeeded.Should().BeTrue(
                $"transactions should recover within {this.recoveryTimeout}; "
                + $"the last probe succeeded={recovery.LastProbeSucceeded}, "
                + $"remaining groups={recovery.RemainingGroupCount}, elapsed={recovery.Elapsed}");

            this.Log(
                $"Recovery phase=final-validation started, timestamp={DateTime.UtcNow:O}. "
                + $"Performed {Volatile.Read(ref index)} transactions on each group.");
            var validationStartedAt = Stopwatch.GetTimestamp();
            await ValidateResults(txGrains, transactionGroups);
            this.Log(
                $"Recovery phase=final-validation completed, timestamp={DateTime.UtcNow:O}, "
                + $"elapsed={Stopwatch.GetElapsedTime(validationStartedAt)}. Transaction results validated.");
        }

        private static Task WakeupGrains(List<ITransactionalBitArrayGrain> grains)
        {
            var tasks =  new List<Task>();
            foreach (var grain in grains)
            {
                tasks.Add(grain.Ping());
            }
            return Task.WhenAll(tasks);
        }

        private async Task<List<ExpectedGrainActivity>[]?> RunWhileSucceeding(
            List<ExpectedGrainActivity>[] transactionGroups,
            Func<int> getIndex,
            CancellationTokenSource stopProducing,
            TaskCompletionSource<TransactionFailure> firstFailure,
            TaskCompletionSource<InFlightBatch> firstInFlightBatch)
        {
            while (!stopProducing.IsCancellationRequested)
            {
                var transactionIndex = getIndex();
                var failed = await RunAllTxReportFailed(
                    transactionGroups,
                    transactionIndex,
                    failure =>
                    {
                        if (firstFailure.TrySetResult(failure))
                        {
                            stopProducing.Cancel();
                        }
                    },
                    tasks =>
                    {
                        var pendingCount = tasks.Count(task => !task.IsCompleted);
                        if (pendingCount > 0)
                        {
                            firstInFlightBatch.TrySetResult(new(transactionIndex, pendingCount));
                        }
                    });

                if (failed is not null)
                {
                    return failed;
                }
            }

            return null;
        }

        private async Task<RecoveryResult> RecoverTransactions(
            List<ExpectedGrainActivity>[] transactionGroups,
            Func<int> getIndex,
            TimeSpan timeout,
            TimeSpan cleanupTimeout,
            TransactionRecoveryEventObserver recoveryEvents)
        {
            var startedAt = Stopwatch.GetTimestamp();
            var deadline = startedAt + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            var remainingGroups = transactionGroups;
            var attempts = 0;
            var lastTransactionIndex = -1;
            var waitForTransitionAfter = recoveryEvents.LatestRelevantSequence;
            var timelineLogCursor = 0L;
            var probeRequiresTransition = false;

            while (Stopwatch.GetTimestamp() < deadline)
            {
                if (probeRequiresTransition)
                {
                    var transition = await recoveryEvents.WaitForNextTransitionAsync(waitForTransitionAfter, deadline);
                    waitForTransitionAfter = transition.Sequence;
                    if (transition.Sequence > timelineLogCursor)
                    {
                        this.Log(
                            $"Recovery phase=transaction-event, timestamp={DateTime.UtcNow:O}. "
                            + TransactionRecoveryEventObserver.FormatTransition(transition).Trim());
                        timelineLogCursor = transition.Sequence;
                    }
                }

                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    break;
                }

                lastTransactionIndex = getIndex();
                attempts++;
                var attemptStartedAt = Stopwatch.GetTimestamp();
                var eventSequenceBeforeProbe = recoveryEvents.LatestRelevantSequence;
                var groupsBeingProbed = remainingGroups;
                this.Log(
                    $"Recovery phase=transaction-probe started, timestamp={DateTime.UtcNow:O}, "
                    + $"attempt={attempts}, index={lastTransactionIndex}, groups={FormatGroups(groupsBeingProbed)}.");
                var probeTask = RunAllTxReportFailed(groupsBeingProbed, lastTransactionIndex);
                List<ExpectedGrainActivity>[]? failedGroups;
                try
                {
                    var now = Stopwatch.GetTimestamp();
                    failedGroups = await probeTask.WaitAsync(Stopwatch.GetElapsedTime(now, deadline));
                }
                catch (TimeoutException)
                {
                    var cleanup = await Task.WhenAny(probeTask, Task.Delay(cleanupTimeout));
                    var cleanupSettled = ReferenceEquals(cleanup, probeTask);
                    if (cleanupSettled)
                    {
                        await probeTask;
                    }
                    else
                    {
                        probeTask.Ignore();
                    }

                    throw new TimeoutException(
                        $"Transaction recovery probe {attempts} did not settle before the {timeout} watchdog. "
                        + $"Index={lastTransactionIndex}, groups={FormatGroups(groupsBeingProbed)}, "
                        + $"cleanupTimeout={cleanupTimeout}, cleanupSettled={cleanupSettled}."
                        + Environment.NewLine
                        + recoveryEvents.FormatTimeline());
                }

                var attemptElapsed = Stopwatch.GetElapsedTime(attemptStartedAt);
                var elapsed = Stopwatch.GetElapsedTime(startedAt);
                this.Log(
                    $"Recovery phase=transaction-probe completed, timestamp={DateTime.UtcNow:O}, "
                    + $"attempt={attempts}, index={lastTransactionIndex}, groups={FormatGroups(groupsBeingProbed)}, "
                    + $"succeeded={failedGroups is null}, attemptElapsed={attemptElapsed}, totalElapsed={elapsed}.");
                LogNewTransitions(recoveryEvents, ref timelineLogCursor);

                if (failedGroups is null)
                {
                    return new RecoveryResult(
                        elapsed < timeout,
                        true,
                        attempts,
                        0,
                        lastTransactionIndex,
                        elapsed);
                }

                remainingGroups = failedGroups;
                recoveryEvents.SetRelevantGrains(
                    remainingGroups.SelectMany(group => group).Select(grain => grain.RuntimeGrainId));
                waitForTransitionAfter = eventSequenceBeforeProbe;
                probeRequiresTransition = true;
            }

            LogNewTransitions(recoveryEvents, ref timelineLogCursor);
            return new RecoveryResult(
                false,
                false,
                attempts,
                remainingGroups.Length,
                lastTransactionIndex,
                Stopwatch.GetElapsedTime(startedAt));
        }

        // Runs all transactions and returns failed;
        private async Task<List<ExpectedGrainActivity>[]?> RunAllTxReportFailed(
            List<ExpectedGrainActivity>[] transactionGroups,
            int index,
            Action<TransactionFailure>? onFailure = null,
            Action<IReadOnlyList<Task>>? onStarted = null)
        {
            var pending = transactionGroups
                .Select(group => (Task: SetBit(group, index), Group: group))
                .ToList();
            var failureObservers = onFailure is null
                ? []
                : pending
                    .Select(item => TransactionRecoveryFailureObservation.ObserveAsync(
                        item.Task,
                        (exception, observedAt) => onFailure(
                            new TransactionFailure(
                                index,
                                item.Group.Select(activity => activity.GrainId).ToArray(),
                                exception,
                                observedAt))))
                    .ToArray();
            onStarted?.Invoke(pending.Select(item => item.Task).ToArray());

            var failedGroups = new List<List<ExpectedGrainActivity>>();
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending.Select(item => item.Task));
                var completedIndex = pending.FindIndex(item => ReferenceEquals(item.Task, completed));
                var transactionGroup = pending[completedIndex].Group;
                pending.RemoveAt(completedIndex);

                try
                {
                    await completed;
                }
                catch (Exception)
                {
                    failedGroups.Add(transactionGroup);
                }
            }
            await Task.WhenAll(failureObservers);

            if (failedGroups.Count == 0)
            {
                return null;
            }

            var result = failedGroups.ToArray();
            this.Log(
                $"Some transactions failed. Index: {index}. {result.Length} out of {transactionGroups.Length} failed. "
                + $"Failed groups: {string.Join(", ", result.Select(transactionGroup => string.Join(":", transactionGroup.Select(a => a.GrainId))))}");
            return result;
        }

        private static string FormatGroups(IEnumerable<List<ExpectedGrainActivity>> groups)
            => string.Join(",", groups.Select(group => $"[{string.Join(":", group.Select(grain => grain.GrainId))}]"));

        private void LogNewTransitions(TransactionRecoveryEventObserver observer, ref long cursor)
        {
            foreach (var transition in observer.GetTimeline())
            {
                if (transition.Sequence <= cursor)
                {
                    continue;
                }

                this.Log(
                    $"Recovery phase=transaction-event, timestamp={DateTime.UtcNow:O}. "
                    + TransactionRecoveryEventObserver.FormatTransition(transition).Trim());
                cursor = transition.Sequence;
            }
        }

        private async Task<bool> AllTxSucceed(List<ExpectedGrainActivity>[] transactionGroups, int index)
        {
            // null return indicates none failed
            return (await RunAllTxReportFailed(transactionGroups, index) == null);
        }

        private async Task SetBit(List<ExpectedGrainActivity> grains, int index)
        {
            try
            {
                await this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid()).MultiGrainSetBit(grains.Select(v => v.Grain).ToList(), index);
                grains.ForEach(g =>
                {
                    g.Expected.Set(index, true);
                    g.Unambiguous.Set(index, true);
                });
            }
            catch (OrleansTransactionAbortedException e)
            {
                this.Log($"Some transactions failed. Index: {index}: Exception: {e.GetType().Name}: {e.Message}");
                grains.ForEach(g =>
                {
                    g.Expected.Set(index, false);
                    g.Unambiguous.Set(index, true);
                });
                throw;
            }
            catch (Exception e)
            {
                this.Log($"Ambiguous transaction failure. Index: {index}: Exception: {e.GetType().Name}: {e.Message}");
                grains.ForEach(g =>
                {
                    g.Expected.Set(index, false);
                    g.Unambiguous.Set(index, false);
                });
                throw;
            }
        }

        private async Task ValidateResults(List<ExpectedGrainActivity> txGrains, List<ExpectedGrainActivity>[] transactionGroups)
        {
            await Task.WhenAll(txGrains.Select(a => a.GetActual()));
            this.Log($"Got all {txGrains.Count} actual values");

            bool pass = true;
            foreach (List<ExpectedGrainActivity> transactionGroup in transactionGroups)
            {
                if (transactionGroup.Count == 0) continue;
                BitArrayState first = transactionGroup[0].Actual.FirstOrDefault()!;
                foreach (ExpectedGrainActivity activity in transactionGroup.Skip(1))
                {
                    BitArrayState actual = activity.Actual.FirstOrDefault()!;
                    BitArrayState difference = first ^ actual;
                    if (difference.Value.Any(v => v != 0))
                    {
                        this.Log($"Activity on grain {activity.GrainId} did not match activity on {transactionGroup[0].GrainId}:\n"
                                 + $"{first} ^\n"
                                 + $"{actual} = \n"
                                 + $"{difference}\n"
                                 + $"Activation: {activity.GrainId}");
                        pass = false;
                    }

                }
            }

            int i = 0;
            foreach (ExpectedGrainActivity activity in txGrains)
            {
                BitArrayState expected = activity.Expected;
                BitArrayState unambiguous = activity.Unambiguous;
                BitArrayState unambuguousExpected = expected & unambiguous;
                List<BitArrayState> actual = activity.Actual;
                BitArrayState? first = actual.FirstOrDefault();
                if (first == null)
                {
                    this.Log($"No activity for {i} ({activity.GrainId})");
                    pass = false;
                    continue;
                }

                int j = 0;
                foreach (BitArrayState result in actual)
                {
                    // skip comparing first to first.
                    if (ReferenceEquals(first, result)) continue;
                    // Check if each state is identical to the first state.
                    var difference = result ^ first;
                    if (difference.Value.Any(v => v != 0))
                    {
                        this.Log($"Activity on grain {i}, state {j} did not match 'first':\n"
                                 + $"  {first}\n"
                                 + $"^ {result}\n"
                                 + $"= {difference}\n"
                                 + $"Activation: {activity.GrainId}");
                        pass = false;
                    }

                    j++;
                }

                // Check if the unambiguous portions of the first match.
                var unambiguousFirst = first & unambiguous;
                var unambiguousDifference = unambuguousExpected ^ unambiguousFirst;

                if (unambiguousDifference.Value.Any(v => v != 0))
                {
                    this.Log(
                        $"First state on grain {i} did not match 'expected':\n"
                        + $"  {unambuguousExpected}\n"
                        + $"^ {unambiguousFirst}\n"
                        + $"= {unambiguousDifference}\n"
                        + $"Activation: {activity.GrainId}");
                    pass = false;
                }

                i++;
            }
            this.Log($"Report complete : {pass}");
            pass.Should().BeTrue();
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "{Message}"
        )]
        private static partial void LogInformationMessage(ILogger logger, string message);
    }
}

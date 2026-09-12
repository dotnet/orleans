using System.Reflection;
using CsCheck;
using Orleans.Runtime.ClusterServices;
using Orleans.Runtime.GrainDirectory;
using TestExtensions;
using Xunit;

namespace UnitTests.ClusterServices;

[TestArea("Runtime")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
public sealed class PartitionTransitionCoordinatorTests
{
    private static readonly RingRange Range = RingRange.Create(100, 200);
    private static readonly ClusterServiceViewId View1 = new(0, new(1));
    private static readonly ClusterServiceViewId View2 = new(0, new(2));

    [Fact]
    public async Task Acquisition_BlocksUntilStateAndFenceAreInstalled()
    {
        var transitions = new DirectoryTransitions();
        var acquisition = new DirectoryAcquisition(Range, View1, View2);
        transitions.Add(Range, acquisition);

        Assert.Equal(Range, acquisition.Range);
        Assert.Equal(View1, acquisition.PreviousView);
        Assert.Equal(View2, acquisition.TargetView);
        Assert.Equal(AcquisitionPhase.AwaitingState, acquisition.Phase);
        Assert.False(transitions.TryGetBlockingTransition(Range, View1, out _));
        Assert.True(transitions.TryGetBlockingTransition(Range, View2, out var wait));
        Assert.Same(acquisition.Completion, wait);
        Assert.Throws<InvalidOperationException>(acquisition.Complete);
        Assert.Throws<InvalidOperationException>(() => acquisition.MarkFenced(new(ClusterServiceFencingMode.External, 42)));
        Assert.Null(acquisition.Fence);
        Assert.Equal(TransitionGateStatus.Pending, acquisition.Status);
        Assert.False(wait.IsCompleted);

        acquisition.MarkStateInstalled();
        Assert.Equal(AcquisitionPhase.StateInstalled, acquisition.Phase);
        Assert.Throws<InvalidOperationException>(acquisition.MarkStateInstalled);
        Assert.Throws<InvalidOperationException>(acquisition.Complete);
        Assert.Equal(TransitionGateStatus.Pending, acquisition.Status);
        Assert.False(wait.IsCompleted);

        var fence = new ClusterServiceFence(ClusterServiceFencingMode.External, 42);
        acquisition.MarkFenced(fence);
        Assert.Equal(AcquisitionPhase.Fenced, acquisition.Phase);
        Assert.Equal(fence, acquisition.Fence);
        Assert.Throws<InvalidOperationException>(() => acquisition.MarkFenced(new(ClusterServiceFencingMode.External, 43)));
        Assert.Equal(fence, acquisition.Fence);
        Assert.True(acquisition.IsBlocking);
        acquisition.Complete();

        await wait;
        Assert.Equal(TransitionGateStatus.Completed, acquisition.Status);
        Assert.False(acquisition.IsBlocking);
        Assert.False(transitions.TryGetBlockingTransition(Range, View2, out var released));
        Assert.Same(Task.CompletedTask, released);
        Assert.Throws<InvalidOperationException>(acquisition.Complete);
        Assert.Throws<InvalidOperationException>(acquisition.MarkStateInstalled);
        Assert.Throws<InvalidOperationException>(() => acquisition.Fail(new Exception("too late")));
        transitions.Prune();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Release_DrainsBeforeOptionalRetentionAndCompletion(bool retain)
    {
        var transitions = new DirectoryTransitions();
        var release = new DirectoryRelease(Range, View1, View2);
        transitions.Add(Range, release);
        Assert.Equal(Range, release.Range);
        Assert.Equal(View1, release.PreviousView);
        Assert.Equal(ReleasePhase.Blocking, release.Phase);
        Assert.Throws<InvalidOperationException>(release.MarkStateRetained);
        Assert.Throws<InvalidOperationException>(release.Complete);
        Assert.Equal(TransitionGateStatus.Pending, release.Status);

        release.MarkDrained();
        Assert.Equal(ReleasePhase.Drained, release.Phase);
        Assert.Throws<InvalidOperationException>(release.MarkDrained);
        Assert.True(transitions.TryGetBlockingTransition(Range, View2, out var wait));
        Assert.Same(release.Completion, wait);
        Assert.False(wait.IsCompleted);
        if (retain)
        {
            release.MarkStateRetained();
            Assert.Equal(ReleasePhase.StateRetained, release.Phase);
            Assert.Throws<InvalidOperationException>(release.MarkStateRetained);
            Assert.False(wait.IsCompleted);
            Assert.True(release.IsBlocking);
        }

        release.Complete();
        await wait;
        Assert.Equal(TransitionGateStatus.Completed, release.Status);
        Assert.False(release.IsBlocking);
        Assert.Throws<InvalidOperationException>(release.MarkStateRetained);
        Assert.False(transitions.TryGetBlockingTransition(Range, View2, out _));
    }

    [Fact]
    public async Task Barrier_BlocksExactAndNewerViewsUntilCompletion()
    {
        var transitions = new DirectoryTransitions();
        var barrier = new DirectoryBarrier(Range, View2);
        transitions.Add(Range, barrier);
        Assert.Equal(Range, barrier.Range);
        Assert.False(transitions.TryGetBlockingTransition(Range, View1, out var preceding));
        Assert.Same(Task.CompletedTask, preceding);
        Assert.True(transitions.TryGetBlockingTransition(Range, View2, out var exact));
        Assert.True(transitions.TryGetBlockingTransition(Range, new(0, new(3)), out var newer));
        Assert.Same(barrier.Completion, exact);
        Assert.Same(exact, newer);

        barrier.Complete();
        await exact;
        Assert.Equal(TransitionGateStatus.Completed, barrier.Status);
        Assert.False(transitions.TryGetBlockingTransition(Range, new(0, new(3)), out _));
    }

    [Fact]
    public void TypedRoles_ExcludeWrongRoleMethodsAndSealReadinessValidation()
    {
        var common = typeof(TransitionGate<ClusterServiceViewId>);
        Assert.True(common.IsAbstract);
        foreach (var method in new[] { "Complete", "MarkStateInstalled", "MarkFenced", "MarkDrained", "MarkStateRetained" })
        {
            Assert.Null(common.GetMethod(method));
        }

        Assert.Null(common.GetProperty("PreviousView"));
        Assert.Null(common.GetProperty("Failure"));
        Assert.Null(typeof(OwnershipAcquisition<long>).GetMethod("MarkDrained"));
        Assert.Null(typeof(OwnershipAcquisition<long>).GetMethod("MarkStateRetained"));
        Assert.Null(typeof(OwnershipRelease<long>).GetMethod("MarkStateInstalled"));
        Assert.Null(typeof(OwnershipRelease<long>).GetMethod("MarkFenced"));
        Assert.Null(typeof(OwnershipRelease<long>).GetProperty("Fence"));
        Assert.Null(typeof(ViewBarrier<long>).GetProperty("PreviousView"));
        foreach (var method in new[] { "MarkStateInstalled", "MarkFenced", "MarkDrained", "MarkStateRetained" })
        {
            Assert.Null(typeof(ViewBarrier<long>).GetMethod(method));
        }

        foreach (var role in new[] { typeof(OwnershipAcquisition<long>), typeof(OwnershipRelease<long>), typeof(ViewBarrier<long>) })
        {
            Assert.False(role.IsSealed);
            Assert.False(role.GetMethod("Complete")!.IsVirtual);
            var validation = role.GetMethod("ValidateCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.True(validation.IsVirtual);
            Assert.True(validation.IsFinal);
            Assert.Equal(role, validation.DeclaringType);
        }

        Assert.True(typeof(DirectoryAcquisition).IsSealed);
        Assert.True(typeof(DirectoryRelease).IsSealed);
        Assert.True(typeof(DirectoryBarrier).IsSealed);
        Assert.True(typeof(DirectoryTransitions).IsSealed);
    }

    [Fact]
    public void Constructors_RejectNonIncreasingViewsAndAcceptComparableReferenceIds()
    {
        Assert.Throws<ArgumentException>(() => new OwnershipAcquisition<long>(2, 2));
        Assert.Throws<ArgumentException>(() => new OwnershipAcquisition<long>(2, 1));
        Assert.Throws<ArgumentException>(() => new OwnershipRelease<long>(2, 2));
        Assert.Throws<ArgumentException>(() => new OwnershipRelease<long>(2, 1));
        Assert.Throws<ArgumentNullException>(() => new ViewBarrier<string>(null!));
        Assert.Throws<ArgumentNullException>(() => new OwnershipAcquisition<string>(null!, "b"));
        Assert.Throws<ArgumentNullException>(() => new OwnershipRelease<string>("a", null!));
        var acquisition = new OwnershipAcquisition<string>("a", "b");
        Assert.Equal("a", acquisition.PreviousView);
        Assert.Equal("b", acquisition.TargetView);
        Assert.Equal(TransitionGateStatus.Pending, new ViewBarrier<long>(0).Status);
    }

    [Fact]
    public async Task CompletionValidationFailure_RemainsPendingThenFailsWithOriginalException()
    {
        var transitions = new DirectoryTransitions();
        var acquisition = new DirectoryAcquisition(Range, View1, View2);
        transitions.Add(Range, acquisition);
        var failure = Assert.Throws<InvalidOperationException>(acquisition.Complete);
        Assert.Equal(TransitionGateStatus.Pending, acquisition.Status);
        Assert.False(acquisition.Completion.IsCompleted);
        Assert.True(transitions.TryGetBlockingTransition(Range, View2, out var wait));

        acquisition.Fail(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => wait));
        Assert.Equal(TransitionGateStatus.Failed, acquisition.Status);
        Assert.True(acquisition.IsBlocking);
        transitions.Prune();
        Assert.True(transitions.TryGetBlockingTransition(Range, View2, out var failed));
        Assert.Same(wait, failed);
        Assert.Throws<InvalidOperationException>(acquisition.MarkStateInstalled);
        Assert.Throws<InvalidOperationException>(() => acquisition.MarkFenced(new(ClusterServiceFencingMode.External, 1)));
        Assert.Throws<InvalidOperationException>(acquisition.Complete);
        Assert.Throws<InvalidOperationException>(() => acquisition.Fail(new ArgumentException("replacement")));
        Assert.Equal(AcquisitionPhase.AwaitingState, acquisition.Phase);
        Assert.Null(acquisition.Fence);
        Assert.Same(failure, Assert.Single(failed.Exception!.InnerExceptions));

        transitions.AbortAll(TestContext.Current.CancellationToken);
        Assert.Equal(TransitionGateStatus.Aborted, acquisition.Status);
        Assert.False(acquisition.IsBlocking);
        Assert.False(transitions.TryGetBlockingTransition(Range, View2, out _));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => wait));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryShutdown_TransferFinallyCanAbortAgainWithoutReplacingCompletion(bool failBeforeShutdown)
    {
        var transitions = new DirectoryTransitions();
        var acquisition = new DirectoryAcquisition(Range, View1, View2);
        transitions.Add(Range, acquisition);
        var completion = acquisition.Completion;
        InvalidOperationException? failure = null;
        if (failBeforeShutdown)
        {
            failure = Assert.Throws<InvalidOperationException>(acquisition.Complete);
            Assert.Equal(TransitionGateStatus.Pending, acquisition.Status);
            acquisition.Fail(failure);
            transitions.Prune();
            Assert.True(transitions.TryGetBlockingTransition(Range, View2, out var blocked));
            Assert.Same(completion, blocked);
        }

        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        transitions.AbortAll(shutdown.Token);
        Assert.Equal(TransitionGateStatus.Aborted, acquisition.Status);
        Assert.False(transitions.TryGetBlockingTransition(Range, View2, out _));

        acquisition.Abort(TestContext.Current.CancellationToken);
        transitions.Prune();
        transitions.AbortAll(shutdown.Token);

        Assert.Equal(TransitionGateStatus.Aborted, acquisition.Status);
        Assert.False(acquisition.IsBlocking);
        Assert.Same(completion, acquisition.Completion);
        Assert.False(transitions.TryGetBlockingTransition(Range, View2, out var released));
        Assert.Same(Task.CompletedTask, released);
        Assert.Throws<InvalidOperationException>(acquisition.Complete);
        if (failBeforeShutdown)
        {
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => completion));
        }
        else
        {
            var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
            Assert.Equal(shutdown.Token, cancellation.CancellationToken);
        }
    }

    [Fact]
    public async Task FailedReleaseAndBarrier_CannotProgressOrReplaceOriginalFailure()
    {
        var release = new OwnershipRelease<long>(1, 2);
        var barrier = new ViewBarrier<long>(2);
        var failure = new ApplicationException("original");
        release.MarkDrained();
        release.Fail(failure);
        barrier.Fail(failure);

        Assert.Throws<InvalidOperationException>(release.MarkStateRetained);
        Assert.Throws<InvalidOperationException>(release.MarkDrained);
        Assert.Throws<InvalidOperationException>(release.Complete);
        Assert.Equal(ReleasePhase.Drained, release.Phase);
        Assert.Throws<InvalidOperationException>(barrier.Complete);
        Assert.Throws<InvalidOperationException>(() => barrier.Fail(new Exception("replacement")));
        Assert.Throws<ArgumentNullException>(() => new ViewBarrier<long>(1).Fail(null!));

        foreach (var gate in new TransitionGate<long>[] { release, barrier })
        {
            Assert.Equal(TransitionGateStatus.Failed, gate.Status);
            Assert.True(gate.IsBlocking);
            Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(() => gate.Completion));
            gate.Abort(TestContext.Current.CancellationToken);
            gate.Abort(TestContext.Current.CancellationToken);
            Assert.Equal(TransitionGateStatus.Aborted, gate.Status);
            Assert.False(gate.IsBlocking);
            Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(() => gate.Completion));
        }
    }

    [Fact]
    public async Task Abort_PreservesCanceledTokenAndDoesNotChangeCompletedGates()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var canceled = new ViewBarrier<long>(1);
        canceled.Abort(source.Token);
        var preserved = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.Completion);
        Assert.Equal(source.Token, preserved.CancellationToken);
        Assert.Equal(TransitionGateStatus.Aborted, canceled.Status);

        using var activeSource = new CancellationTokenSource();
        var synthesized = new OwnershipAcquisition<long>(1, 2);
        synthesized.Abort(activeSource.Token);
        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synthesized.Completion);
        Assert.True(cancellation.CancellationToken.IsCancellationRequested);
        Assert.False(activeSource.IsCancellationRequested);
        Assert.NotEqual(activeSource.Token, cancellation.CancellationToken);
        Assert.Throws<InvalidOperationException>(synthesized.MarkStateInstalled);
        Assert.Throws<InvalidOperationException>(synthesized.Complete);
        Assert.Equal(AcquisitionPhase.AwaitingState, synthesized.Phase);

        var completed = new ViewBarrier<long>(1);
        completed.Complete();
        completed.Abort(source.Token);
        Assert.Equal(TransitionGateStatus.Completed, completed.Status);
        await completed.Completion;
    }

    [Fact]
    public async Task CallerCancellation_DoesNotCancelSharedProgressOrOtherWaiters()
    {
        var map = new RangeTransitionGateMap<long>();
        var gate = new ViewBarrier<long>(2);
        map.Add(Range, gate);
        Assert.True(map.TryGetBlockingTransition(Range, 2, out var shared));
        using var caller = new CancellationTokenSource();
        var canceledWait = shared.WaitAsync(caller.Token);
        var unaffectedWait = shared.WaitAsync(TestContext.Current.CancellationToken);
        caller.Cancel();

        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);
        Assert.Equal(caller.Token, cancellation.CancellationToken);
        Assert.Equal(TransitionGateStatus.Pending, gate.Status);
        Assert.True(map.IsBlocked(Range, 2));
        Assert.False(shared.IsCompleted);
        Assert.False(unaffectedWait.IsCompleted);

        gate.Complete();
        await unaffectedWait;
        Assert.True(shared.IsCompletedSuccessfully);
        Assert.False(map.IsBlocked(Range, 2));
    }

    [Theory]
    [InlineData("Fence", "Complete", false, TransitionGateStatus.Completed, AcquisitionPhase.Fenced, false, false)]
    [InlineData("Complete", "Fence", false, TransitionGateStatus.Pending, AcquisitionPhase.Fenced, true, false)]
    [InlineData("Fence", "Fail", false, TransitionGateStatus.Failed, AcquisitionPhase.Fenced, false, false)]
    [InlineData("Fail", "Fence", false, TransitionGateStatus.Failed, AcquisitionPhase.StateInstalled, false, true)]
    [InlineData("Fence", "Abort", false, TransitionGateStatus.Aborted, AcquisitionPhase.Fenced, false, false)]
    [InlineData("Abort", "Fence", false, TransitionGateStatus.Aborted, AcquisitionPhase.StateInstalled, false, true)]
    [InlineData("Complete", "Fail", true, TransitionGateStatus.Completed, AcquisitionPhase.Fenced, false, true)]
    [InlineData("Fail", "Complete", true, TransitionGateStatus.Failed, AcquisitionPhase.Fenced, false, true)]
    [InlineData("Complete", "Abort", true, TransitionGateStatus.Completed, AcquisitionPhase.Fenced, false, false)]
    [InlineData("Abort", "Complete", true, TransitionGateStatus.Aborted, AcquisitionPhase.Fenced, false, true)]
    [InlineData("Fail", "Abort", true, TransitionGateStatus.Aborted, AcquisitionPhase.Fenced, false, false)]
    [InlineData("Abort", "Fail", true, TransitionGateStatus.Aborted, AcquisitionPhase.Fenced, false, true)]
    public async Task Acquisition_ConcurrentOperationsSerializeAtTheGateLock(
        string first,
        string second,
        bool initiallyFenced,
        object expectedStatusValue,
        object expectedPhaseValue,
        bool firstRejected,
        bool secondRejected)
    {
        var expectedStatus = (TransitionGateStatus)expectedStatusValue;
        var expectedPhase = (AcquisitionPhase)expectedPhaseValue;
        var gate = new ControlledAcquisition();
        gate.MarkStateInstalled();
        var fence = new ClusterServiceFence(ClusterServiceFencingMode.External, 42);
        if (initiallyFenced)
        {
            gate.MarkFenced(fence);
        }

        var failure = new ApplicationException("concurrent failure");
        Action Operation(string name) => name switch
        {
            "Fence" => () => gate.MarkFenced(fence),
            "Complete" => gate.Complete,
            "Fail" => () => gate.Fail(failure),
            "Abort" => () => gate.Abort(),
            _ => throw new InvalidOperationException(name)
        };

        var (firstError, contender) = gate.RunBeforeContender(Operation(first), Operation(second));
        var secondError = await contender.WaitAsync(TestContext.Current.CancellationToken);
        AssertRejection(firstRejected, firstError);
        AssertRejection(secondRejected, secondError);
        Assert.Equal(expectedStatus, gate.Status);
        Assert.Equal(expectedPhase, gate.Phase);
        Assert.Equal(expectedPhase is AcquisitionPhase.Fenced ? fence : (ClusterServiceFence?)null, gate.Fence);
        Assert.Equal(expectedStatus is TransitionGateStatus.Pending or TransitionGateStatus.Failed, gate.IsBlocking);
        if (first == "Fail" || second == "Fail" && !secondRejected)
        {
            Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(() => gate.Completion));
        }
        else if (expectedStatus is TransitionGateStatus.Aborted)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.Completion);
        }
        else
        {
            Assert.Equal(expectedStatus is TransitionGateStatus.Completed, gate.Completion.IsCompletedSuccessfully);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Release_ConcurrentDrainAndCompletionValidateUnderTheSameLock(bool drainFirst)
    {
        var gate = new ControlledRelease();
        var first = drainFirst ? (Action)gate.MarkDrained : gate.Complete;
        var second = drainFirst ? (Action)gate.Complete : gate.MarkDrained;
        var (firstError, contender) = gate.RunBeforeContender(first, second);
        var secondError = await contender.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Null(secondError);
        AssertRejection(!drainFirst, firstError);
        Assert.Equal(ReleasePhase.Drained, gate.Phase);
        Assert.Equal(drainFirst ? TransitionGateStatus.Completed : TransitionGateStatus.Pending, gate.Status);
        Assert.Equal(drainFirst, gate.Completion.IsCompletedSuccessfully);
        if (!drainFirst)
        {
            gate.Complete();
        }

        await gate.Completion;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Acquisition_ConcurrentInstallAndTerminationCannotReopenGate(bool terminateFirst, bool abort)
    {
        var gate = new ControlledAcquisition();
        var failure = new ApplicationException("install race");
        Action terminate = abort ? () => gate.Abort() : () => gate.Fail(failure);
        var (firstError, contender) = gate.RunBeforeContender(
            terminateFirst ? terminate : gate.MarkStateInstalled,
            terminateFirst ? gate.MarkStateInstalled : terminate);

        Assert.Null(firstError);
        AssertRejection(terminateFirst, await contender.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(terminateFirst ? AcquisitionPhase.AwaitingState : AcquisitionPhase.StateInstalled, gate.Phase);
        Assert.Equal(abort ? TransitionGateStatus.Aborted : TransitionGateStatus.Failed, gate.Status);
        Assert.Equal(!abort, gate.IsBlocking);
        if (abort)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.Completion);
        }
        else
        {
            Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(() => gate.Completion));
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Release_ConcurrentPhaseAndTerminationCannotReopenGate(bool retain, bool terminateFirst, bool abort)
    {
        var gate = new ControlledRelease();
        if (retain)
        {
            gate.MarkDrained();
        }

        var failure = new ApplicationException("release race");
        Action advance = retain ? gate.MarkStateRetained : gate.MarkDrained;
        Action terminate = abort ? () => gate.Abort() : () => gate.Fail(failure);
        var (firstError, contender) = gate.RunBeforeContender(
            terminateFirst ? terminate : advance,
            terminateFirst ? advance : terminate);
        Assert.Null(firstError);
        AssertRejection(terminateFirst, await contender.WaitAsync(TestContext.Current.CancellationToken));
        var expectedPhase = retain
            ? terminateFirst ? ReleasePhase.Drained : ReleasePhase.StateRetained
            : terminateFirst ? ReleasePhase.Blocking : ReleasePhase.Drained;
        Assert.Equal(expectedPhase, gate.Phase);
        Assert.Equal(abort ? TransitionGateStatus.Aborted : TransitionGateStatus.Failed, gate.Status);
        Assert.Equal(!abort, gate.IsBlocking);
        if (abort)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.Completion);
        }
        else
        {
            Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(() => gate.Completion));
        }
    }

    [Fact]
    public void CsCheck_ValidTypedSequencesAlwaysReleaseTheirGate()
    {
        Gen.Int.Array[32].Sample(
            choices =>
            {
                var map = new RangeTransitionGateMap<long>();
                var version = 1L;
                foreach (var choice in choices)
                {
                    var previous = version++;
                    if ((choice & 1) == 0)
                    {
                        var acquisition = new OwnershipAcquisition<long>(previous, version);
                        map.Add(Range, acquisition);
                        acquisition.MarkStateInstalled();
                        acquisition.MarkFenced(new(ClusterServiceFencingMode.External, version));
                        Assert.True(map.IsBlocked(Range, version));
                        acquisition.Complete();
                        Assert.Equal(TransitionGateStatus.Completed, acquisition.Status);
                    }
                    else
                    {
                        var release = new OwnershipRelease<long>(previous, version);
                        map.Add(Range, release);
                        release.MarkDrained();
                        if ((choice & 2) != 0)
                        {
                            release.MarkStateRetained();
                        }

                        Assert.True(map.IsBlocked(Range, version));
                        release.Complete();
                        Assert.Equal(TransitionGateStatus.Completed, release.Status);
                    }

                    Assert.False(map.IsBlocked(Range, version));
                }
            },
            seed: "cluster-service-transition-v1",
            iter: 100,
            threads: 1,
            print: static choices => $"choices=[{string.Join(',', choices)}]");
    }

    private sealed class ControlledAcquisition() : OwnershipAcquisition<long>(1, 2)
    {
        public (Exception? FirstError, Task<Exception?> Contender) RunBeforeContender(Action first, Action second) =>
            RunBeforeContenderCore(SyncRoot, first, second);
    }

    private static void AssertRejection(bool rejected, Exception? exception)
    {
        if (rejected)
        {
            Assert.IsType<InvalidOperationException>(exception);
        }
        else
        {
            Assert.Null(exception);
        }
    }

    private sealed class ControlledRelease() : OwnershipRelease<long>(1, 2)
    {
        public (Exception? FirstError, Task<Exception?> Contender) RunBeforeContender(Action first, Action second) =>
            RunBeforeContenderCore(SyncRoot, first, second);
    }

    private static (Exception? FirstError, Task<Exception?> Contender) RunBeforeContenderCore(
        object syncRoot,
        Action first,
        Action second)
    {
        var started = new ManualResetEventSlim();
        lock (syncRoot)
        {
            var contender = Task.Run(() =>
            {
                using (started)
                {
                    started.Set();
                    return Record.Exception(second);
                }
            });
            Assert.True(started.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
                "The contender must be armed while the first operation holds the gate lock.");
            var firstError = Record.Exception(first);
            Assert.False(contender.IsCompleted);
            return (firstError, contender);
        }
    }
}

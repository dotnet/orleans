using CsCheck;
using Orleans.Runtime;
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
    private static readonly ClusterServiceViewId View1 = new(new(1), 1, "config");
    private static readonly ClusterServiceViewId View2 = new(new(2), 1, "config");

    [Fact]
    public async Task InboundTransition_BlocksTargetViewUntilStateAndFenceAreInstalled()
    {
        var coordinator = new PartitionTransitionCoordinator();
        var transition = coordinator.BeginInbound(Range, View1, View2);

        Assert.False(coordinator.IsBlocked(Range, View1.MembershipVersion));
        Assert.True(coordinator.TryGetBlockingTransition(Range, View2.MembershipVersion, out var wait));
        Assert.False(wait.IsCompleted);
        Assert.Throws<InvalidOperationException>(transition.Complete);

        transition.MarkStateInstalled();
        transition.MarkFenced(new(ClusterServiceFencingMode.External, 42));
        transition.Complete();

        await wait;
        Assert.Equal(PartitionTransitionStage.Completed, transition.Stage);
        Assert.Equal(new ClusterServiceFence(ClusterServiceFencingMode.External, 42), transition.Fence);
        Assert.False(coordinator.IsBlocked(Range, View2.MembershipVersion));
    }

    [Fact]
    public async Task OutboundTransition_DrainsBeforeRetainingStateAndOpeningGate()
    {
        var coordinator = new PartitionTransitionCoordinator();
        var transition = coordinator.BeginOutbound(Range, View1, View2);
        Assert.True(coordinator.TryGetBlockingTransition(Range, View2.MembershipVersion, out var wait));
        Assert.Same(transition.Completion, wait);

        transition.MarkDrained();

        Assert.Equal(PartitionTransitionStage.Drained, transition.Stage);
        Assert.True(coordinator.IsBlocked(Range, View2.MembershipVersion));
        Assert.True(coordinator.TryGetBlockingTransition(Range, View2.MembershipVersion, out var drainedWait));
        Assert.Same(transition.Completion, drainedWait);
        Assert.False(wait.IsCompleted);
        Assert.False(transition.Completion.IsCompleted);

        transition.MarkStateRetained();

        Assert.Equal(PartitionTransitionStage.StateRetained, transition.Stage);
        Assert.True(coordinator.IsBlocked(Range, View2.MembershipVersion));
        Assert.True(coordinator.TryGetBlockingTransition(Range, View2.MembershipVersion, out var retainedWait));
        Assert.Same(transition.Completion, retainedWait);
        Assert.False(wait.IsCompleted);
        Assert.False(transition.Completion.IsCompleted);

        transition.Complete();

        await wait;
        Assert.Equal(PartitionTransitionStage.Completed, transition.Stage);
        Assert.False(coordinator.IsBlocked(Range, View2.MembershipVersion));
    }

    [Fact]
    public async Task AbortedTransition_CancelsWaitersAndRemovesGate()
    {
        var coordinator = new PartitionTransitionCoordinator();
        var transition = coordinator.BeginInbound(Range, View1, View2);
        Assert.True(coordinator.TryGetBlockingTransition(Range, View2.MembershipVersion, out var wait));

        transition.Abort(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(PartitionTransitionStage.Aborted, transition.Stage);
        Assert.False(coordinator.IsBlocked(Range, View2.MembershipVersion));
    }

    [Fact]
    public void OverlappingTransitions_InSameTargetViewAreRejected()
    {
        var coordinator = new PartitionTransitionCoordinator();
        var original = coordinator.BeginInbound(Range, View1, View2);

        Assert.Throws<InvalidOperationException>(() =>
            coordinator.BeginOutbound(RingRange.Create(150, 250), View1, View2));

        Assert.Equal(PartitionTransitionStage.Blocking, original.Stage);
        Assert.False(original.Completion.IsCompleted);
        Assert.True(coordinator.TryGetBlockingTransition(Range, View2.MembershipVersion, out var completion));
        Assert.Same(original.Completion, completion);
    }

    [Fact]
    public void CsCheck_ValidTransitionSequences_AlwaysReleaseTheirGate()
    {
        Gen.Int.Array[32].Sample(
            choices => VerifyValidSequences(choices),
            seed: "cluster-service-transition-v1",
            iter: 100,
            threads: 1,
            print: static choices => $"choices=[{string.Join(',', choices)}]");
    }

    private static void VerifyValidSequences(int[] choices)
    {
        var coordinator = new PartitionTransitionCoordinator();
        var version = 1L;
        foreach (var choice in choices)
        {
            var previous = new ClusterServiceViewId(new(version), 1, "config");
            var current = new ClusterServiceViewId(new(++version), 1, "config");
            PartitionTransition transition;
            if ((choice & 1) == 0)
            {
                transition = coordinator.BeginInbound(Range, previous, current);
                transition.MarkStateInstalled();
                transition.MarkFenced(new(ClusterServiceFencingMode.External, version));
            }
            else
            {
                transition = coordinator.BeginOutbound(Range, previous, current);
                transition.MarkDrained();
                if ((choice & 2) != 0)
                {
                    transition.MarkStateRetained();
                }
            }

            Assert.True(coordinator.IsBlocked(Range, current.MembershipVersion));
            transition.Complete();
            Assert.Equal(PartitionTransitionStage.Completed, transition.Stage);
            Assert.False(coordinator.IsBlocked(Range, current.MembershipVersion));
        }
    }

    [Fact]
    public async Task TwoCoordinators_BlockAdvanceAbortAndCompleteIndependently()
    {
        var rangeA = RingRange.Create(100, 200);
        var rangeB = RingRange.Create(300, 400);
        var coordinatorA = new PartitionTransitionCoordinator();
        var coordinatorB = new PartitionTransitionCoordinator();
        var transitionA = coordinatorA.BeginInbound(rangeA, View1, View2);
        var transitionB = coordinatorB.BeginOutbound(rangeB, View1, View2);
        Assert.True(coordinatorA.TryGetBlockingTransition(rangeA, View2.MembershipVersion, out var waitA));
        Assert.True(coordinatorB.TryGetBlockingTransition(rangeB, View2.MembershipVersion, out var waitB));

        transitionA.MarkStateInstalled();
        Assert.Equal(PartitionTransitionStage.Blocking, transitionB.Stage);
        Assert.True(coordinatorB.IsBlocked(rangeB, View2.MembershipVersion));
        Assert.False(waitB.IsCompleted);

        transitionA.MarkFenced(new(ClusterServiceFencingMode.External, 101));
        transitionA.Complete();
        await waitA;
        Assert.Equal(PartitionTransitionStage.Completed, transitionA.Stage);
        Assert.Equal(PartitionTransitionStage.Blocking, transitionB.Stage);
        Assert.True(coordinatorB.IsBlocked(rangeB, View2.MembershipVersion));
        Assert.False(waitB.IsCompleted);

        var abortedA = coordinatorA.BeginOutbound(rangeA, View1, View2);
        Assert.True(coordinatorA.IsBlocked(rangeA, View2.MembershipVersion));
        abortedA.Abort(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abortedA.Completion);
        Assert.Equal(PartitionTransitionStage.Aborted, abortedA.Stage);
        Assert.Equal(PartitionTransitionStage.Blocking, transitionB.Stage);
        Assert.True(coordinatorB.IsBlocked(rangeB, View2.MembershipVersion));
        Assert.False(waitB.IsCompleted);

        transitionB.MarkDrained();
        transitionB.Complete();
        await waitB;
        Assert.Equal(PartitionTransitionStage.Completed, transitionB.Stage);
        Assert.False(coordinatorB.IsBlocked(rangeB, View2.MembershipVersion));
    }

    [Fact]
    public void InvalidStageAndDirectionActions_AreRejectedWithoutStateOrBlockingChanges()
    {
        var inboundCoordinator = new PartitionTransitionCoordinator();
        var inbound = inboundCoordinator.BeginInbound(Range, View1, View2);
        AssertRejectedWithoutMutation(inboundCoordinator, inbound, inbound.MarkDrained);
        AssertRejectedWithoutMutation(inboundCoordinator, inbound, inbound.MarkStateRetained);
        AssertRejectedWithoutMutation(
            inboundCoordinator,
            inbound,
            () => inbound.MarkFenced(new(ClusterServiceFencingMode.External, 11)));
        AssertRejectedWithoutMutation(inboundCoordinator, inbound, inbound.Complete);
        inbound.MarkStateInstalled();
        AssertRejectedWithoutMutation(inboundCoordinator, inbound, inbound.MarkStateInstalled);
        AssertRejectedWithoutMutation(inboundCoordinator, inbound, inbound.MarkDrained);
        AssertRejectedWithoutMutation(inboundCoordinator, inbound, inbound.Complete);
        inbound.MarkFenced(new(ClusterServiceFencingMode.TimedSafetyLease, 12));
        AssertRejectedWithoutMutation(inboundCoordinator, inbound, inbound.MarkStateInstalled);
        AssertRejectedWithoutMutation(
            inboundCoordinator,
            inbound,
            () => inbound.MarkFenced(new(ClusterServiceFencingMode.External, 13)));
        inbound.Complete();

        var outboundCoordinator = new PartitionTransitionCoordinator();
        var outbound = outboundCoordinator.BeginOutbound(Range, View1, View2);
        AssertRejectedWithoutMutation(outboundCoordinator, outbound, outbound.MarkStateInstalled);
        AssertRejectedWithoutMutation(
            outboundCoordinator,
            outbound,
            () => outbound.MarkFenced(new(ClusterServiceFencingMode.External, 21)));
        AssertRejectedWithoutMutation(outboundCoordinator, outbound, outbound.MarkStateRetained);
        AssertRejectedWithoutMutation(outboundCoordinator, outbound, outbound.Complete);
        outbound.MarkDrained();
        AssertRejectedWithoutMutation(outboundCoordinator, outbound, outbound.MarkDrained);
        AssertRejectedWithoutMutation(outboundCoordinator, outbound, outbound.MarkStateInstalled);
        outbound.MarkStateRetained();
        AssertRejectedWithoutMutation(outboundCoordinator, outbound, outbound.MarkStateRetained);
        outbound.Complete();

        var barrierCoordinator = new PartitionTransitionCoordinator();
        var barrier = barrierCoordinator.BeginBarrier(Range, View1);
        AssertRejectedWithoutMutation(barrierCoordinator, barrier, barrier.MarkDrained);
        AssertRejectedWithoutMutation(barrierCoordinator, barrier, barrier.MarkStateRetained);
        AssertRejectedWithoutMutation(barrierCoordinator, barrier, barrier.MarkStateInstalled);
        AssertRejectedWithoutMutation(
            barrierCoordinator,
            barrier,
            () => barrier.MarkFenced(new(ClusterServiceFencingMode.MembershipView, 31)));
        barrier.Complete();

        Assert.Equal(PartitionTransitionStage.Completed, inbound.Stage);
        Assert.Equal(PartitionTransitionStage.Completed, outbound.Stage);
        Assert.Equal(PartitionTransitionStage.Completed, barrier.Stage);
    }

    [Fact]
    public void Begin_RejectsEmptyAndNonIncreasingViewsWithoutInstallingGate()
    {
        var coordinator = new PartitionTransitionCoordinator();
        var equalVersion = new ClusterServiceViewId(View1.MembershipVersion, 2, "other");
        var olderVersion = new ClusterServiceViewId(new(0), 1, "config");

        Assert.Throws<ArgumentException>(() => coordinator.BeginInbound(RingRange.Empty, View1, View2));
        Assert.Throws<ArgumentException>(() => coordinator.BeginInbound(Range, View1, equalVersion));
        Assert.Throws<ArgumentException>(() => coordinator.BeginOutbound(Range, View1, olderVersion));

        Assert.False(coordinator.IsBlocked(Range, View1.MembershipVersion));
        Assert.False(coordinator.IsBlocked(Range, View2.MembershipVersion));
        Assert.False(coordinator.TryGetBlockingTransition(Range, View2.MembershipVersion, out var completion));
        Assert.Same(Task.CompletedTask, completion);
    }

    [Fact]
    public void WrapAroundRangeAndRequestVersion_BlockOnlyIntersectingEqualOrNewerRequests()
    {
        var coordinator = new PartitionTransitionCoordinator();
        var wrapped = RingRange.Create(300, 100);
        var highOverlap = RingRange.Create(350, 400);
        var lowOverlap = RingRange.Create(0, 50);
        var disjoint = RingRange.Create(150, 250);
        var transition = coordinator.BeginInbound(wrapped, View1, View2);

        Assert.False(coordinator.IsBlocked(highOverlap, View1.MembershipVersion));
        Assert.True(coordinator.IsBlocked(highOverlap, View2.MembershipVersion));
        Assert.True(coordinator.IsBlocked(lowOverlap, new(3)));
        Assert.False(coordinator.IsBlocked(disjoint, new(3)));
        Assert.False(coordinator.TryGetBlockingTransition(disjoint, new(3), out var completed));
        Assert.Same(Task.CompletedTask, completed);
        Assert.True(coordinator.TryGetBlockingTransition(lowOverlap, View2.MembershipVersion, out var blocking));
        Assert.Same(transition.Completion, blocking);
        Assert.False(blocking.IsCompleted);

        transition.Abort(TestContext.Current.CancellationToken);

        Assert.False(coordinator.IsBlocked(highOverlap, View2.MembershipVersion));
        Assert.True(transition.Completion.IsCanceled);
    }

    [Fact]
    public void CsCheck_CoordinatorsPreservePerPartitionRangeAndVersionIsolation()
    {
        Gen.Int.Array[24].Sample(
            VerifyCoordinatorIsolationHistory,
            seed: "partition-transition-isolation-v1",
            iter: 100,
            threads: 1,
            print: PrintIsolationHistory);
    }

    private static void AssertRejectedWithoutMutation(
        PartitionTransitionCoordinator coordinator,
        PartitionTransition transition,
        Action action)
    {
        var expectedStage = transition.Stage;
        var expectedFence = transition.Fence;
        var expectedFailure = transition.Failure;
        Assert.True(coordinator.TryGetBlockingTransition(
            transition.Range,
            transition.TargetView.MembershipVersion,
            out var expectedCompletion));
        Assert.Same(transition.Completion, expectedCompletion);

        Assert.Throws<InvalidOperationException>(action);

        Assert.Equal(expectedStage, transition.Stage);
        Assert.Equal(expectedFence, transition.Fence);
        Assert.Same(expectedFailure, transition.Failure);
        Assert.True(coordinator.TryGetBlockingTransition(
            transition.Range,
            transition.TargetView.MembershipVersion,
            out var actualCompletion));
        Assert.Same(expectedCompletion, actualCompletion);
        Assert.False(actualCompletion.IsCompleted);
    }

    private static void VerifyCoordinatorIsolationHistory(int[] choices)
    {
        var inputA = new RangeInput(3_000_000_000, 500_000_000);
        var inputB = new RangeInput(1_000_000_000, 2_000_000_000);
        var rangeA = RingRange.Create(inputA.Start, inputA.End);
        var rangeB = RingRange.Create(inputB.Start, inputB.End);
        var coordinatorA = new PartitionTransitionCoordinator();
        var coordinatorB = new PartitionTransitionCoordinator();
        var transitionA = coordinatorA.BeginInbound(rangeA, CreateView(4), CreateView(5));
        var transitionB = coordinatorB.BeginOutbound(rangeB, CreateView(6), CreateView(7));

        foreach (var choice in choices)
        {
            var operation = unchecked((uint)choice) % 6;
            var stageA = transitionA.Stage;
            var stageB = transitionB.Stage;
            switch (operation)
            {
                case 0 when stageA == PartitionTransitionStage.Blocking:
                    transitionA.MarkStateInstalled();
                    break;
                case 0 when stageA == PartitionTransitionStage.StateInstalled:
                    transitionA.MarkFenced(new(ClusterServiceFencingMode.External, choice));
                    break;
                case 0 when stageA == PartitionTransitionStage.Fenced:
                    transitionA.Complete();
                    break;
                case 1 when stageA is not (PartitionTransitionStage.Completed or PartitionTransitionStage.Aborted):
                    transitionA.Abort();
                    break;
                case 2 when stageB == PartitionTransitionStage.Blocking:
                    transitionB.MarkDrained();
                    break;
                case 2 when stageB == PartitionTransitionStage.Drained:
                    transitionB.MarkStateRetained();
                    break;
                case 2 when stageB == PartitionTransitionStage.StateRetained:
                    transitionB.Complete();
                    break;
                case 3 when stageB is not (PartitionTransitionStage.Completed or PartitionTransitionStage.Aborted):
                    transitionB.Abort();
                    break;
            }

            if (operation <= 1)
            {
                Assert.Equal(stageB, transitionB.Stage);
            }
            else if (operation <= 3)
            {
                Assert.Equal(stageA, transitionA.Stage);
            }

            var raw = unchecked((uint)choice);
            var queryInput = new RangeInput(
                raw * 2_654_435_761u,
                System.Numerics.BitOperations.RotateLeft(raw ^ 0xA5A5_A5A5u, 13));
            var query = RingRange.Create(queryInput.Start, queryInput.End);
            var requestVersion = new MembershipVersion(raw % 10);
            var expectedA = IsActive(transitionA.Stage)
                && requestVersion.Value >= 5
                && RingRangeIntersectionOracle(inputA, queryInput);
            var expectedB = IsActive(transitionB.Stage)
                && requestVersion.Value >= 7
                && RingRangeIntersectionOracle(inputB, queryInput);

            Assert.Equal(expectedA, coordinatorA.IsBlocked(query, requestVersion));
            Assert.Equal(expectedB, coordinatorB.IsBlocked(query, requestVersion));
            Assert.Equal(IsActive(transitionA.Stage), !transitionA.Completion.IsCompleted);
            Assert.Equal(IsActive(transitionB.Stage), !transitionB.Completion.IsCompleted);
        }
    }

    private static bool IsActive(PartitionTransitionStage stage) =>
        stage is not (PartitionTransitionStage.Completed or PartitionTransitionStage.Aborted);

    private static bool RingRangeIntersectionOracle(RangeInput left, RangeInput right)
    {
        Span<LinearSegment> leftSegments = stackalloc LinearSegment[2];
        Span<LinearSegment> rightSegments = stackalloc LinearSegment[2];
        var leftCount = Linearize(left, leftSegments);
        var rightCount = Linearize(right, rightSegments);
        for (var i = 0; i < leftCount; i++)
        {
            for (var j = 0; j < rightCount; j++)
            {
                if (leftSegments[i].Start <= rightSegments[j].End
                    && rightSegments[j].Start <= leftSegments[i].End)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int Linearize(RangeInput input, Span<LinearSegment> segments)
    {
        if (input.Start == input.End)
        {
            if (input.Start == 0)
            {
                return 0;
            }

            segments[0] = new(0, uint.MaxValue);
            return 1;
        }

        if (input.Start < input.End)
        {
            segments[0] = new(input.Start + 1, input.End);
            return 1;
        }

        var count = 0;
        if (input.Start < uint.MaxValue)
        {
            segments[count++] = new(input.Start + 1, uint.MaxValue);
        }

        segments[count++] = new(0, input.End);
        return count;
    }

    private static string PrintIsolationHistory(int[] choices) =>
        $"history=[{string.Join(
            "; ",
            choices.Select(static choice =>
            {
                var raw = unchecked((uint)choice);
                var queryStart = raw * 2_654_435_761u;
                var queryEnd = System.Numerics.BitOperations.RotateLeft(raw ^ 0xA5A5_A5A5u, 13);
                return $"op={raw % 6},query=({queryStart},{queryEnd}],version={raw % 10}";
            }))}]";

    private static ClusterServiceViewId CreateView(long version) => new(new(version), 1, "config");

    private readonly record struct RangeInput(uint Start, uint End);

    private readonly record struct LinearSegment(uint Start, uint End);

    [Fact]
    public async Task Fail_PreservesExactFailureFaultsCompletionAndRemovesGate()
    {
        var coordinator = new PartitionTransitionCoordinator();
        var transition = coordinator.BeginBarrier(Range, View2);
        var failure = new InvalidOperationException("deterministic transition failure");
        var completion = transition.Completion;

        transition.Fail(failure);

        Assert.Same(failure, transition.Failure);
        Assert.Equal(PartitionTransitionStage.Failed, transition.Stage);
        Assert.Same(completion, transition.Completion);
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => completion);
        Assert.Same(failure, observed);
        Assert.False(coordinator.IsBlocked(Range, View2.MembershipVersion));
        Assert.False(coordinator.TryGetBlockingTransition(Range, View2.MembershipVersion, out var released));
        Assert.Same(Task.CompletedTask, released);

        var repeatedFailure = new ArgumentException("must not replace the first failure");
        var rejection = Assert.Throws<InvalidOperationException>(() => transition.Fail(repeatedFailure));

        Assert.Equal("The transition is no longer active.", rejection.Message);
        Assert.Same(failure, transition.Failure);
        Assert.Equal(PartitionTransitionStage.Failed, transition.Stage);
        Assert.Same(completion, transition.Completion);

        var completionRejection = Assert.Throws<InvalidOperationException>(transition.Complete);

        Assert.Equal("A barrier transition must remain blocked until completion.", completionRejection.Message);
        Assert.Same(failure, transition.Failure);
        Assert.Equal(PartitionTransitionStage.Failed, transition.Stage);

        var abortRejection = Assert.Throws<InvalidOperationException>(
            () => transition.Abort(TestContext.Current.CancellationToken));

        Assert.Equal("The transition is no longer active.", abortRejection.Message);
        Assert.Equal(PartitionTransitionStage.Failed, transition.Stage);
        Assert.Same(failure, transition.Failure);
        Assert.Same(completion, transition.Completion);
        Assert.False(coordinator.IsBlocked(Range, View2.MembershipVersion));
    }

    [Theory]
    [InlineData(
        (int)PartitionTransitionDirection.Inbound,
        "An inbound transition must install state and establish fencing before activation.")]
    [InlineData(
        (int)PartitionTransitionDirection.Outbound,
        "An outbound transition must drain operations before completion.")]
    public async Task Complete_RejectsFailedInboundAndOutboundAfterReleasingGate(
        int directionValue,
        string expectedMessage)
    {
        var direction = (PartitionTransitionDirection)directionValue;
        var coordinator = new PartitionTransitionCoordinator();
        var transition = direction == PartitionTransitionDirection.Inbound
            ? coordinator.BeginInbound(Range, View1, View2)
            : coordinator.BeginOutbound(Range, View1, View2);
        var failure = new InvalidOperationException($"failed {direction}");

        transition.Fail(failure);
        var rejection = Assert.Throws<InvalidOperationException>(transition.Complete);

        Assert.Equal(expectedMessage, rejection.Message);
        Assert.Same(failure, transition.Failure);
        Assert.Equal(PartitionTransitionStage.Failed, transition.Stage);
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => transition.Completion);
        Assert.Same(failure, observed);
        Assert.False(coordinator.IsBlocked(Range, View2.MembershipVersion));
        Assert.False(coordinator.TryGetBlockingTransition(Range, View2.MembershipVersion, out var released));
        Assert.Same(Task.CompletedTask, released);
    }

    [Fact]
    public async Task Begin_AllowsIndependentDisjointSameTargetAndOverlappingDifferentTargetTransitions()
    {
        var coordinator = new PartitionTransitionCoordinator();
        var firstRange = RingRange.Create(100, 200);
        var disjointRange = RingRange.Create(300, 400);
        var overlappingRange = RingRange.Create(150, 250);
        var firstOnlyRange = RingRange.Create(100, 150);
        var overlapOnlyRange = RingRange.Create(200, 250);
        var view3 = CreateView(3);

        var first = coordinator.BeginBarrier(firstRange, View2);
        var sameTargetDisjoint = coordinator.BeginBarrier(disjointRange, View2);
        var differentTargetOverlapping = coordinator.BeginBarrier(overlappingRange, view3);

        Assert.NotSame(first.Completion, sameTargetDisjoint.Completion);
        Assert.NotSame(first.Completion, differentTargetOverlapping.Completion);
        Assert.NotSame(sameTargetDisjoint.Completion, differentTargetOverlapping.Completion);
        Assert.True(coordinator.TryGetBlockingTransition(firstOnlyRange, View2.MembershipVersion, out var firstGate));
        Assert.Same(first.Completion, firstGate);
        Assert.True(coordinator.TryGetBlockingTransition(disjointRange, View2.MembershipVersion, out var disjointGate));
        Assert.Same(sameTargetDisjoint.Completion, disjointGate);
        Assert.True(coordinator.TryGetBlockingTransition(overlapOnlyRange, view3.MembershipVersion, out var overlappingGate));
        Assert.Same(differentTargetOverlapping.Completion, overlappingGate);

        first.Complete();
        await firstGate;

        Assert.True(first.Completion.IsCompletedSuccessfully);
        Assert.False(sameTargetDisjoint.Completion.IsCompleted);
        Assert.False(differentTargetOverlapping.Completion.IsCompleted);
        Assert.False(coordinator.IsBlocked(firstOnlyRange, View2.MembershipVersion));
        Assert.True(coordinator.IsBlocked(disjointRange, View2.MembershipVersion));
        Assert.True(coordinator.IsBlocked(overlapOnlyRange, view3.MembershipVersion));

        sameTargetDisjoint.Complete();
        await disjointGate;

        Assert.True(sameTargetDisjoint.Completion.IsCompletedSuccessfully);
        Assert.False(differentTargetOverlapping.Completion.IsCompleted);
        Assert.False(coordinator.IsBlocked(disjointRange, View2.MembershipVersion));
        Assert.True(coordinator.IsBlocked(overlapOnlyRange, view3.MembershipVersion));

        differentTargetOverlapping.Complete();
        await overlappingGate;

        Assert.True(differentTargetOverlapping.Completion.IsCompletedSuccessfully);
        Assert.False(coordinator.IsBlocked(overlapOnlyRange, view3.MembershipVersion));
    }

    [Fact]
    public async Task Abort_PreservesCanceledTokenAndSynthesizesCanceledTokenForNonCanceledInput()
    {
        var coordinator = new PartitionTransitionCoordinator();
        using var canceledSource = new CancellationTokenSource();
        canceledSource.Cancel();
        var canceledToken = canceledSource.Token;
        var canceledTransition = coordinator.BeginBarrier(Range, View1);

        canceledTransition.Abort(canceledToken);
        var preserved = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceledTransition.Completion);

        Assert.Equal(canceledToken, preserved.CancellationToken);
        Assert.True(preserved.CancellationToken.IsCancellationRequested);
        Assert.Equal(PartitionTransitionStage.Aborted, canceledTransition.Stage);
        Assert.False(coordinator.IsBlocked(Range, View1.MembershipVersion));

        using var activeSource = new CancellationTokenSource();
        var activeToken = activeSource.Token;
        var synthesizedTransition = coordinator.BeginBarrier(Range, View2);

        synthesizedTransition.Abort(activeToken);
        var synthesized = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => synthesizedTransition.Completion);

        Assert.False(activeToken.IsCancellationRequested);
        Assert.True(synthesized.CancellationToken.IsCancellationRequested);
        Assert.NotEqual(activeToken, synthesized.CancellationToken);
        Assert.Equal(PartitionTransitionStage.Aborted, synthesizedTransition.Stage);
        Assert.False(coordinator.IsBlocked(Range, View2.MembershipVersion));
    }

    [Fact]
    public async Task Barrier_BlocksExactAndNewerVersionsButNotPrecedingVersionUntilCompletion()
    {
        var coordinator = new PartitionTransitionCoordinator();
        var barrier = coordinator.BeginBarrier(Range, View2);
        var newerVersion = new MembershipVersion(3);

        Assert.False(coordinator.IsBlocked(Range, View1.MembershipVersion));
        Assert.False(coordinator.TryGetBlockingTransition(Range, View1.MembershipVersion, out var precedingGate));
        Assert.Same(Task.CompletedTask, precedingGate);
        Assert.True(coordinator.IsBlocked(Range, View2.MembershipVersion));
        Assert.True(coordinator.TryGetBlockingTransition(Range, View2.MembershipVersion, out var exactGate));
        Assert.Same(barrier.Completion, exactGate);
        Assert.True(coordinator.IsBlocked(Range, newerVersion));
        Assert.True(coordinator.TryGetBlockingTransition(Range, newerVersion, out var newerGate));
        Assert.Same(barrier.Completion, newerGate);
        Assert.False(exactGate.IsCompleted);

        barrier.Complete();
        await exactGate;

        Assert.True(barrier.Completion.IsCompletedSuccessfully);
        Assert.Equal(PartitionTransitionStage.Completed, barrier.Stage);
        Assert.False(coordinator.IsBlocked(Range, View1.MembershipVersion));
        Assert.False(coordinator.IsBlocked(Range, View2.MembershipVersion));
        Assert.False(coordinator.IsBlocked(Range, newerVersion));
        Assert.False(coordinator.TryGetBlockingTransition(Range, newerVersion, out var releasedGate));
        Assert.Same(Task.CompletedTask, releasedGate);
    }
}

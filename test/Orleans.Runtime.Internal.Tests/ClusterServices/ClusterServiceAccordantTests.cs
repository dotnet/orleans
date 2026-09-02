using Microsoft.Accordant;
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
public sealed class ClusterServiceAccordantTests
{
    [Fact]
    public async Task Accordant_PartitionTransitionStateMachine_CoversInboundOutboundAndAbortPaths()
    {
        var spec = new TransitionBehavioralSpec();
        var initialState = TransitionModelState.Create();
        var coverage = new TransitionExecutionCoverage();
        var testCases = spec.GenerateTests(
                initialState,
                spec.CreateInputSet(),
                new TestGenerationOptions
                {
                    MaxDepth = 5,
                    SequentialTestCaseAlgorithm = SequentialTestCaseAlgorithms.CreateTransitionCoverage(maxSequenceLength: 7),
                    ShouldApply = (input, state) => TransitionBehavioralSpec.CanApply(
                        (TransitionRequest)input.Request,
                        (TransitionModelState)state)
                })
            .ToList();
        var context = spec.CreateTestingContext();
        context.RequestPrinter = request => request?.ToString() ?? "<null>";
        context.ResponsePrinter = response => response?.ToString() ?? "<null>";
        var results = await spec.RunTests(
            context,
            initialState,
            testCases,
            new TestExecutionOptions
            {
                StopOnFirstFailure = true,
                BeforeEach = info => info.Context.Register(new TransitionExecutionHarness(coverage))
            });
        var failure = results.FirstOrDefault(result => !result.Success);

        Assert.NotEmpty(testCases);
        Assert.True(
            failure is null && results.All(result => result.Success),
            $"cases={testCases.Count}; failure={failure?.LastFailureMessage}");
        Assert.Contains(TransitionOperationKind.BeginInbound, coverage.ExecutedKinds);
        Assert.Contains(TransitionOperationKind.Install, coverage.ExecutedKinds);
        Assert.Contains(TransitionOperationKind.Fence, coverage.ExecutedKinds);
        Assert.Contains(TransitionOperationKind.BeginOutbound, coverage.ExecutedKinds);
        Assert.Contains(TransitionOperationKind.Drain, coverage.ExecutedKinds);
        Assert.Contains(TransitionOperationKind.Retain, coverage.ExecutedKinds);
        Assert.Contains(TransitionOperationKind.Complete, coverage.ExecutedKinds);
        Assert.Contains(TransitionOperationKind.Abort, coverage.ExecutedKinds);
        Assert.Contains(TransitionOperationKind.Validate, coverage.ExecutedKinds);
        Assert.Contains(TransitionExecutionPath.InboundInstall, coverage.ExecutedPaths);
        Assert.Contains(TransitionExecutionPath.InboundFence, coverage.ExecutedPaths);
        Assert.Contains(TransitionExecutionPath.InboundComplete, coverage.ExecutedPaths);
        Assert.Contains(TransitionExecutionPath.OutboundDrain, coverage.ExecutedPaths);
        Assert.Contains(TransitionExecutionPath.OutboundRetain, coverage.ExecutedPaths);
        Assert.Contains(TransitionExecutionPath.OutboundComplete, coverage.ExecutedPaths);
        Assert.Contains(TransitionExecutionPath.Abort, coverage.ExecutedPaths);
    }

    private sealed class TransitionBehavioralSpec : Spec<TransitionModelState>
    {
        private readonly TransitionOperation _operation = new();

        public TransitionBehavioralSpec() => Add(_operation);

        public InputSet CreateInputSet()
        {
            var result = new InputSet();
            foreach (var operation in Enum.GetValues<TransitionOperationKind>())
            {
                result.Add(_operation.With(new TransitionRequest(operation), operation.ToString()));
            }

            return result;
        }

        public static bool CanApply(TransitionRequest request, TransitionModelState state) =>
            request.Kind switch
            {
                TransitionOperationKind.BeginInbound or TransitionOperationKind.BeginOutbound => !state.Active,
                TransitionOperationKind.Install =>
                    state.Active
                    && state.Direction == (int)PartitionTransitionDirection.Inbound
                    && state.Stage == (int)PartitionTransitionStage.Blocking,
                TransitionOperationKind.Fence =>
                    state.Active
                    && state.Direction == (int)PartitionTransitionDirection.Inbound
                    && state.Stage == (int)PartitionTransitionStage.StateInstalled,
                TransitionOperationKind.Drain =>
                    state.Active
                    && state.Direction == (int)PartitionTransitionDirection.Outbound
                    && state.Stage == (int)PartitionTransitionStage.Blocking,
                TransitionOperationKind.Retain =>
                    state.Active
                    && state.Direction == (int)PartitionTransitionDirection.Outbound
                    && state.Stage == (int)PartitionTransitionStage.Drained,
                TransitionOperationKind.Complete =>
                    state.Active
                    && (state.Stage == (int)PartitionTransitionStage.Fenced
                        || state.Direction == (int)PartitionTransitionDirection.Outbound
                        && state.Stage is (int)PartitionTransitionStage.Drained or (int)PartitionTransitionStage.StateRetained),
                TransitionOperationKind.Abort => state.Active,
                _ => true
            };
    }

    private sealed class TransitionOperation()
        : Operation<TransitionRequest, TransitionResponse, TransitionModelState>("Transition")
    {
        public override ExpectedOutcomes Apply(TransitionRequest request, TransitionModelState state)
        {
            var expected = Predict(request, state);
            return Expect.That(response =>
                    response == expected
                        ? ValidationResult.Valid()
                        : ValidationResult.Invalid($"request={request}; expected={expected}; actual={response}"))
                .ThenState(next => ApplyModel(request, next));
        }

        public override Task<TransitionResponse> ExecuteAsync(TestingContext context, TransitionRequest request) =>
            Task.FromResult(context.Get<TransitionExecutionHarness>().Execute(request));
    }

    private sealed class TransitionExecutionHarness(TransitionExecutionCoverage coverage)
    {
        private static readonly RingRange Range = RingRange.Create(100, 200);
        private readonly PartitionTransitionCoordinator _coordinator = new();
        private PartitionTransition? _transition;
        private PartitionTransitionStage _lastStage = PartitionTransitionStage.Completed;
        private long _version = 1;

        public TransitionResponse Execute(TransitionRequest request)
        {
            coverage.Observe(request.Kind);
            switch (request.Kind)
            {
                case TransitionOperationKind.BeginInbound:
                    _transition = _coordinator.BeginInbound(Range, CreateView(_version), CreateView(++_version));
                    break;
                case TransitionOperationKind.BeginOutbound:
                    _transition = _coordinator.BeginOutbound(Range, CreateView(_version), CreateView(++_version));
                    break;
                case TransitionOperationKind.Install:
                    _transition!.MarkStateInstalled();
                    coverage.Observe(TransitionExecutionPath.InboundInstall);
                    break;
                case TransitionOperationKind.Fence:
                    _transition!.MarkFenced(new(ClusterServiceFencingMode.External, _version));
                    coverage.Observe(TransitionExecutionPath.InboundFence);
                    break;
                case TransitionOperationKind.Drain:
                    _transition!.MarkDrained();
                    coverage.Observe(TransitionExecutionPath.OutboundDrain);
                    break;
                case TransitionOperationKind.Retain:
                    _transition!.MarkStateRetained();
                    coverage.Observe(TransitionExecutionPath.OutboundRetain);
                    break;
                case TransitionOperationKind.Complete:
                    var direction = _transition!.Direction;
                    _transition!.Complete();
                    coverage.Observe(direction == PartitionTransitionDirection.Inbound
                        ? TransitionExecutionPath.InboundComplete
                        : TransitionExecutionPath.OutboundComplete);
                    _lastStage = _transition.Stage;
                    _transition = null;
                    break;
                case TransitionOperationKind.Abort:
                    _transition!.Abort();
                    coverage.Observe(TransitionExecutionPath.Abort);
                    _lastStage = PartitionTransitionStage.Aborted;
                    _transition = null;
                    break;
            }

            var active = _transition is not null;
            var stage = active ? _transition!.Stage : _lastStage;
            return new(
                active,
                active && _coordinator.IsBlocked(Range, new MembershipVersion(_version)),
                (int)stage,
                _version);
        }

        private static ClusterServiceViewId CreateView(long version) => new(new(version), 1, "config");
    }

    private static TransitionResponse Predict(TransitionRequest request, TransitionModelState state)
    {
        var active = state.Active;
        var direction = state.Direction;
        var stage = state.Stage;
        var version = state.Version;
        switch (request.Kind)
        {
            case TransitionOperationKind.BeginInbound:
                active = true;
                direction = (int)PartitionTransitionDirection.Inbound;
                stage = (int)PartitionTransitionStage.Blocking;
                version++;
                break;
            case TransitionOperationKind.BeginOutbound:
                active = true;
                direction = (int)PartitionTransitionDirection.Outbound;
                stage = (int)PartitionTransitionStage.Blocking;
                version++;
                break;
            case TransitionOperationKind.Install:
                stage = (int)PartitionTransitionStage.StateInstalled;
                break;
            case TransitionOperationKind.Fence:
                stage = (int)PartitionTransitionStage.Fenced;
                break;
            case TransitionOperationKind.Drain:
                stage = (int)PartitionTransitionStage.Drained;
                break;
            case TransitionOperationKind.Retain:
                stage = (int)PartitionTransitionStage.StateRetained;
                break;
            case TransitionOperationKind.Complete:
                active = false;
                stage = (int)PartitionTransitionStage.Completed;
                break;
            case TransitionOperationKind.Abort:
                active = false;
                stage = (int)PartitionTransitionStage.Aborted;
                break;
        }

        return new(active, active, stage, version);
    }

    private static void ApplyModel(TransitionRequest request, TransitionModelState state)
    {
        var response = Predict(request, state);
        state.Active = response.Active;
        state.Stage = response.Stage;
        state.Version = response.Version;
        if (request.Kind == TransitionOperationKind.BeginInbound)
        {
            state.Direction = (int)PartitionTransitionDirection.Inbound;
        }
        else if (request.Kind == TransitionOperationKind.BeginOutbound)
        {
            state.Direction = (int)PartitionTransitionDirection.Outbound;
        }
    }

    private enum TransitionOperationKind
    {
        BeginInbound,
        Install,
        Fence,
        Complete,
        BeginOutbound,
        Drain,
        Retain,
        Abort,
        Validate
    }

    private enum TransitionExecutionPath
    {
        InboundInstall,
        InboundFence,
        InboundComplete,
        OutboundDrain,
        OutboundRetain,
        OutboundComplete,
        Abort
    }

    private sealed class TransitionExecutionCoverage
    {
        public HashSet<TransitionOperationKind> ExecutedKinds { get; } = [];

        public HashSet<TransitionExecutionPath> ExecutedPaths { get; } = [];

        public void Observe(TransitionOperationKind kind) => ExecutedKinds.Add(kind);

        public void Observe(TransitionExecutionPath path) => ExecutedPaths.Add(path);
    }

    private sealed record TransitionRequest(TransitionOperationKind Kind)
    {
        public override string ToString() => Kind.ToString();
    }

    private sealed record TransitionResponse(bool Active, bool Blocked, int Stage, long Version);

    [Fact]
    public async Task Accordant_InvalidCommandsAndRangeVersionProbes_PreserveStateOnRejection()
    {
        var spec = new RejectionTransitionBehavioralSpec();
        var initialState = RejectionTransitionModelState.Create();
        var testCases = spec.GenerateTests(
                initialState,
                spec.CreateInputSet(),
                new TestGenerationOptions
                {
                    MaxDepth = 2,
                    SequentialTestCaseAlgorithm = SequentialTestCaseAlgorithms.CreateTransitionCoverage(maxSequenceLength: 3)
                })
            .ToList();
        var coverage = new RejectionTransitionCoverage();
        var context = spec.CreateTestingContext();
        context.RequestPrinter = request => request?.ToString() ?? "<null-request>";
        context.ResponsePrinter = response => response?.ToString() ?? "<null-response>";
        var results = await spec.RunTests(
            context,
            initialState,
            testCases,
            new TestExecutionOptions
            {
                StopOnFirstFailure = true,
                BeforeEach = info =>
                {
                    info.Context.Register(coverage);
                    info.Context.Register(new RejectionTransitionHarness());
                }
            });
        var failure = results.FirstOrDefault(result => !result.Success);

        Assert.NotEmpty(testCases);
        Assert.True(
            failure is null && results.All(result => result.Success),
            $"cases={testCases.Count}; failure={failure?.LastFailureMessage}; log={failure?.LogFilePath}");
        Assert.Contains(RejectionTransitionOperationKind.BeginNonIncreasingView, coverage.RejectedKinds);
        Assert.Contains(RejectionTransitionOperationKind.Install, coverage.RejectedKinds);
        Assert.Contains(RejectionTransitionOperationKind.Fence, coverage.RejectedKinds);
        Assert.Contains(RejectionTransitionOperationKind.Drain, coverage.RejectedKinds);
        Assert.Contains(RejectionTransitionOperationKind.Retain, coverage.RejectedKinds);
        Assert.Contains(RejectionTransitionOperationKind.Complete, coverage.RejectedKinds);
        Assert.Contains(RejectionTransitionOperationKind.Abort, coverage.RejectedKinds);
        Assert.Contains(RejectionTransitionOperationKind.BeginInbound, coverage.RejectedKinds);
        Assert.Contains(RejectionTransitionOperationKind.BeginOutbound, coverage.RejectedKinds);
        Assert.Contains(RejectionTransitionOperationKind.ProbeOlderOverlap, coverage.ExecutedKinds);
        Assert.Contains(RejectionTransitionOperationKind.ProbeEqualOverlap, coverage.ExecutedKinds);
        Assert.Contains(RejectionTransitionOperationKind.ProbeNewerOverlap, coverage.ExecutedKinds);
        Assert.Contains(RejectionTransitionOperationKind.ProbeNewerDisjoint, coverage.ExecutedKinds);
    }
}

[State]
internal partial class TransitionModelState : State
{
    public bool Active { get; set; }

    public int Direction { get; set; }

    public int Stage { get; set; }

    public long Version { get; set; }

    public static TransitionModelState Create() =>
        new()
        {
            Stage = (int)PartitionTransitionStage.Completed,
            Version = 1
        };
}

internal sealed class RejectionTransitionBehavioralSpec : Spec<RejectionTransitionModelState>
{
    private readonly RejectionTransitionOperation _operation = new();

    public RejectionTransitionBehavioralSpec() => Add(_operation);

    public InputSet CreateInputSet()
    {
        var result = new InputSet();
        foreach (var kind in Enum.GetValues<RejectionTransitionOperationKind>())
        {
            result.Add(_operation.With(new(kind), kind.ToString()));
        }

        return result;
    }
}

internal sealed class RejectionTransitionOperation()
    : Operation<RejectionTransitionRequest, RejectionTransitionResponse, RejectionTransitionModelState>(
        "Invalid transition and probe")
{
    public override ExpectedOutcomes Apply(
        RejectionTransitionRequest request,
        RejectionTransitionModelState state)
    {
        var expected = RejectionTransitionModel.Predict(request, state);
        return Expect.That(response =>
                response == expected
                    ? ValidationResult.Valid()
                    : ValidationResult.Invalid(
                        $"request={request}; expected={expected}; actual={response}; "
                        + $"before=(active={state.Active},direction={state.Direction},stage={state.Stage})"))
            .ThenState(next => RejectionTransitionModel.Apply(request, next));
    }

    public override Task<RejectionTransitionResponse> ExecuteAsync(
        TestingContext context,
        RejectionTransitionRequest request)
    {
        var response = context.Get<RejectionTransitionHarness>().Execute(request);
        context.Get<RejectionTransitionCoverage>().Observe(request.Kind, response.Accepted);
        return Task.FromResult(response);
    }
}

internal sealed class RejectionTransitionHarness
{
    private static readonly RingRange TransitionRange = RingRange.Create(100, 200);
    private static readonly RingRange DisjointRange = RingRange.Create(300, 400);
    private static readonly ClusterServiceViewId PreviousView = new(new(1), 1, "config");
    private static readonly ClusterServiceViewId TargetView = new(new(2), 1, "config");
    private readonly PartitionTransitionCoordinator _coordinator = new();
    private PartitionTransition? _transition;

    public RejectionTransitionResponse Execute(RejectionTransitionRequest request)
    {
        var accepted = true;
        bool? probeBlocked = null;
        try
        {
            switch (request.Kind)
            {
                case RejectionTransitionOperationKind.BeginInbound:
                    RepeatBegin(PartitionTransitionDirection.Inbound);
                    break;
                case RejectionTransitionOperationKind.BeginOutbound:
                    RepeatBegin(PartitionTransitionDirection.Outbound);
                    break;
                case RejectionTransitionOperationKind.BeginNonIncreasingView:
                    _transition = _coordinator.BeginInbound(TransitionRange, TargetView, PreviousView);
                    break;
                case RejectionTransitionOperationKind.Install:
                    GetTransition().MarkStateInstalled();
                    break;
                case RejectionTransitionOperationKind.Fence:
                    GetTransition().MarkFenced(new(ClusterServiceFencingMode.External, 42));
                    break;
                case RejectionTransitionOperationKind.Drain:
                    GetTransition().MarkDrained();
                    break;
                case RejectionTransitionOperationKind.Retain:
                    GetTransition().MarkStateRetained();
                    break;
                case RejectionTransitionOperationKind.Complete:
                    GetTransition().Complete();
                    break;
                case RejectionTransitionOperationKind.Abort:
                    GetTransition().Abort();
                    break;
                case RejectionTransitionOperationKind.ProbeOlderOverlap:
                    probeBlocked = _coordinator.IsBlocked(TransitionRange, PreviousView.MembershipVersion);
                    break;
                case RejectionTransitionOperationKind.ProbeEqualOverlap:
                    probeBlocked = _coordinator.IsBlocked(TransitionRange, TargetView.MembershipVersion);
                    break;
                case RejectionTransitionOperationKind.ProbeNewerOverlap:
                    probeBlocked = _coordinator.IsBlocked(TransitionRange, new(3));
                    break;
                case RejectionTransitionOperationKind.ProbeNewerDisjoint:
                    probeBlocked = _coordinator.IsBlocked(DisjointRange, new(3));
                    break;
            }
        }
        catch (InvalidOperationException)
        {
            accepted = false;
        }
        catch (ArgumentException)
        {
            accepted = false;
        }

        var active = _transition is not null
            && _transition.Stage is not (PartitionTransitionStage.Completed or PartitionTransitionStage.Aborted);
        return new(
            accepted,
            active,
            active && _coordinator.IsBlocked(TransitionRange, TargetView.MembershipVersion),
            probeBlocked,
            (int)(_transition?.Direction ?? PartitionTransitionDirection.Barrier),
            (int)(_transition?.Stage ?? PartitionTransitionStage.Completed),
            _transition?.Fence);
    }

    private PartitionTransition GetTransition() =>
        _transition ?? throw new InvalidOperationException("No transition has begun.");

    private static void RepeatBegin(PartitionTransitionDirection direction)
    {
        var coordinator = new PartitionTransitionCoordinator();
        if (direction == PartitionTransitionDirection.Inbound)
        {
            _ = coordinator.BeginInbound(TransitionRange, PreviousView, TargetView);
            _ = coordinator.BeginInbound(TransitionRange, PreviousView, TargetView);
        }
        else
        {
            _ = coordinator.BeginOutbound(TransitionRange, PreviousView, TargetView);
            _ = coordinator.BeginOutbound(TransitionRange, PreviousView, TargetView);
        }
    }
}

internal static class RejectionTransitionModel
{
    public static RejectionTransitionResponse Predict(
        RejectionTransitionRequest request,
        RejectionTransitionModelState state)
    {
        var accepted = CanApply(request.Kind, state);
        var active = state.Active;
        var direction = state.Direction;
        var stage = state.Stage;
        ClusterServiceFence? fence = state.HasFence
            ? new((ClusterServiceFencingMode)state.FenceMode, state.FenceToken)
            : null;
        bool? probeBlocked = null;
        if (accepted)
        {
            switch (request.Kind)
            {
                case RejectionTransitionOperationKind.BeginInbound:
                    active = true;
                    direction = (int)PartitionTransitionDirection.Inbound;
                    stage = (int)PartitionTransitionStage.Blocking;
                    fence = null;
                    break;
                case RejectionTransitionOperationKind.BeginOutbound:
                    active = true;
                    direction = (int)PartitionTransitionDirection.Outbound;
                    stage = (int)PartitionTransitionStage.Blocking;
                    fence = null;
                    break;
                case RejectionTransitionOperationKind.Install:
                    stage = (int)PartitionTransitionStage.StateInstalled;
                    break;
                case RejectionTransitionOperationKind.Fence:
                    stage = (int)PartitionTransitionStage.Fenced;
                    fence = new(ClusterServiceFencingMode.External, 42);
                    break;
                case RejectionTransitionOperationKind.Drain:
                    stage = (int)PartitionTransitionStage.Drained;
                    break;
                case RejectionTransitionOperationKind.Retain:
                    stage = (int)PartitionTransitionStage.StateRetained;
                    break;
                case RejectionTransitionOperationKind.Complete:
                    active = false;
                    stage = (int)PartitionTransitionStage.Completed;
                    break;
                case RejectionTransitionOperationKind.Abort:
                    active = false;
                    stage = (int)PartitionTransitionStage.Aborted;
                    break;
                case RejectionTransitionOperationKind.ProbeOlderOverlap:
                case RejectionTransitionOperationKind.ProbeNewerDisjoint:
                    probeBlocked = false;
                    break;
                case RejectionTransitionOperationKind.ProbeEqualOverlap:
                case RejectionTransitionOperationKind.ProbeNewerOverlap:
                    probeBlocked = active;
                    break;
            }
        }

        return new(
            accepted,
            active,
            active,
            probeBlocked,
            direction,
            stage,
            fence);
    }

    public static void Apply(RejectionTransitionRequest request, RejectionTransitionModelState state)
    {
        var response = Predict(request, state);
        state.Active = response.Active;
        state.Direction = response.Direction;
        state.Stage = response.Stage;
        state.HasFence = response.Fence.HasValue;
        state.FenceMode = (int)(response.Fence?.Mode ?? default);
        state.FenceToken = response.Fence?.Token ?? default;
    }

    private static bool CanApply(
        RejectionTransitionOperationKind kind,
        RejectionTransitionModelState state) =>
        kind switch
        {
            RejectionTransitionOperationKind.BeginInbound
                or RejectionTransitionOperationKind.BeginOutbound => false,
            RejectionTransitionOperationKind.BeginNonIncreasingView => false,
            RejectionTransitionOperationKind.Install =>
                state.Active
                && state.Direction == (int)PartitionTransitionDirection.Inbound
                && state.Stage == (int)PartitionTransitionStage.Blocking,
            RejectionTransitionOperationKind.Fence =>
                state.Active
                && state.Direction == (int)PartitionTransitionDirection.Inbound
                && state.Stage == (int)PartitionTransitionStage.StateInstalled,
            RejectionTransitionOperationKind.Drain =>
                state.Active
                && state.Direction == (int)PartitionTransitionDirection.Outbound
                && state.Stage == (int)PartitionTransitionStage.Blocking,
            RejectionTransitionOperationKind.Retain =>
                state.Active
                && state.Direction == (int)PartitionTransitionDirection.Outbound
                && state.Stage == (int)PartitionTransitionStage.Drained,
            RejectionTransitionOperationKind.Complete =>
                state.Active
                && (state.Direction == (int)PartitionTransitionDirection.Inbound
                    && state.Stage == (int)PartitionTransitionStage.Fenced
                    || state.Direction == (int)PartitionTransitionDirection.Outbound
                    && state.Stage is (int)PartitionTransitionStage.Drained
                        or (int)PartitionTransitionStage.StateRetained),
            RejectionTransitionOperationKind.Abort => state.Active,
            _ => true
        };
}

internal sealed class RejectionTransitionCoverage
{
    public HashSet<RejectionTransitionOperationKind> ExecutedKinds { get; } = [];

    public HashSet<RejectionTransitionOperationKind> RejectedKinds { get; } = [];

    public void Observe(RejectionTransitionOperationKind kind, bool accepted)
    {
        ExecutedKinds.Add(kind);
        if (!accepted)
        {
            RejectedKinds.Add(kind);
        }
    }
}

internal enum RejectionTransitionOperationKind
{
    BeginInbound,
    BeginOutbound,
    BeginNonIncreasingView,
    Install,
    Fence,
    Drain,
    Retain,
    Complete,
    Abort,
    ProbeOlderOverlap,
    ProbeEqualOverlap,
    ProbeNewerOverlap,
    ProbeNewerDisjoint
}

internal sealed record RejectionTransitionRequest(RejectionTransitionOperationKind Kind)
{
    public override string ToString() => $"kind={Kind}";
}

internal sealed record RejectionTransitionResponse(
    bool Accepted,
    bool Active,
    bool Blocked,
    bool? ProbeBlocked,
    int Direction,
    int Stage,
    ClusterServiceFence? Fence);

[State]
internal partial class RejectionTransitionModelState : State
{
    public bool Active { get; set; }

    public int Direction { get; set; }

    public int Stage { get; set; }

    public bool HasFence { get; set; }

    public int FenceMode { get; set; }

    public long FenceToken { get; set; }

    public static RejectionTransitionModelState Create() =>
        new()
        {
            Direction = (int)PartitionTransitionDirection.Barrier,
            Stage = (int)PartitionTransitionStage.Completed
        };
}

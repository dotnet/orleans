using Microsoft.Accordant;
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
    public async Task Accordant_TypedOwnershipStateMachine_CoversAcquisitionReleaseBarrierFailureAndAbort()
    {
        var coverage = await Run(validCommandsOnly: true, maxDepth: 5, sequenceLength: 7);
        foreach (var kind in Enum.GetValues<GateOperationKind>())
        {
            if (kind is not GateOperationKind.BeginNonIncreasingView)
            {
                Assert.Contains(kind, coverage.Executed);
            }
        }

        Assert.Contains(GateModelRole.Acquisition, coverage.CompletedRoles);
        Assert.Contains(GateModelRole.Release, coverage.CompletedRoles);
        Assert.Contains(GateModelRole.Barrier, coverage.CompletedRoles);
        Assert.Contains(GateModelRole.Acquisition, coverage.FailedRoles);
        Assert.Contains(GateModelRole.Release, coverage.FailedRoles);
        Assert.Contains(GateModelRole.Barrier, coverage.FailedRoles);
        Assert.True(coverage.AbortedFailedGate);
    }

    [Fact]
    public async Task Accordant_InvalidCommandsAndRangeViewProbes_PreserveStateOnRejection()
    {
        var coverage = await Run(validCommandsOnly: false, maxDepth: 3, sequenceLength: 4);
        foreach (var kind in new[]
        {
            GateOperationKind.BeginNonIncreasingView,
            GateOperationKind.Install,
            GateOperationKind.Fence,
            GateOperationKind.Drain,
            GateOperationKind.Retain,
            GateOperationKind.Complete,
            GateOperationKind.Fail,
            GateOperationKind.Abort,
            GateOperationKind.BeginAcquisition,
            GateOperationKind.BeginRelease,
            GateOperationKind.BeginBarrier
        })
        {
            Assert.Contains(kind, coverage.Rejected);
        }

        Assert.Contains(GateOperationKind.ProbeOlderOverlap, coverage.Executed);
        Assert.Contains(GateOperationKind.ProbeEqualOverlap, coverage.Executed);
        Assert.Contains(GateOperationKind.ProbeNewerOverlap, coverage.Executed);
        Assert.Contains(GateOperationKind.ProbeNewerDisjoint, coverage.Executed);
    }

    private static async Task<GateExecutionCoverage> Run(bool validCommandsOnly, int maxDepth, int sequenceLength)
    {
        var spec = new GateBehavioralSpec();
        var initial = GateModelState.Create();
        var coverage = new GateExecutionCoverage();
        var cases = spec.GenerateTests(
            initial,
            spec.CreateInputSet(),
            new TestGenerationOptions
            {
                MaxDepth = maxDepth,
                SequentialTestCaseAlgorithm = SequentialTestCaseAlgorithms.CreateTransitionCoverage(sequenceLength),
                ShouldApply = (input, state) => !validCommandsOnly
                    || GateModel.CanApply(((GateRequest)input.Request).Kind, (GateModelState)state)
            }).ToList();
        var context = spec.CreateTestingContext();
        context.RequestPrinter = request => request?.ToString() ?? "<null>";
        context.ResponsePrinter = response => response?.ToString() ?? "<null>";
        var results = await spec.RunTests(
            context,
            initial,
            cases,
            new TestExecutionOptions
            {
                StopOnFirstFailure = true,
                BeforeEach = info => info.Context.Register(new GateExecutionHarness(coverage))
            });
        var failure = results.FirstOrDefault(result => !result.Success);
        Assert.NotEmpty(cases);
        Assert.True(failure is null && results.All(result => result.Success),
            $"cases={cases.Count}; failure={failure?.LastFailureMessage}; log={failure?.LogFilePath}");
        return coverage;
    }
}

internal sealed class GateBehavioralSpec : Spec<GateModelState>
{
    private readonly GateOperation _operation = new();

    public GateBehavioralSpec() => Add(_operation);

    public InputSet CreateInputSet()
    {
        var inputs = new InputSet();
        foreach (var kind in Enum.GetValues<GateOperationKind>())
        {
            inputs.Add(_operation.With(new(kind), kind.ToString()));
        }

        return inputs;
    }
}

internal sealed class GateOperation() : Operation<GateRequest, GateResponse, GateModelState>("Typed ownership gate")
{
    public override ExpectedOutcomes Apply(GateRequest request, GateModelState state)
    {
        var expected = GateModel.Predict(request.Kind, state);
        return Expect.That(response => response == expected
                ? ValidationResult.Valid()
                : ValidationResult.Invalid($"request={request}; expected={expected}; actual={response}"))
            .ThenState(next => GateModel.Apply(request.Kind, next));
    }

    public override Task<GateResponse> ExecuteAsync(TestingContext context, GateRequest request) =>
        Task.FromResult(context.Get<GateExecutionHarness>().Execute(request.Kind));
}

internal sealed class GateExecutionHarness(GateExecutionCoverage coverage)
{
    private static readonly RingRange Range = RingRange.Create(100, 200);
    private static readonly RingRange Disjoint = RingRange.Create(300, 400);
    private readonly RangeTransitionGateMap<long> _map = new();
    private readonly ApplicationException _failure = new("accordant failure");
    private TransitionGate<long>? _gate;

    public GateResponse Execute(GateOperationKind kind)
    {
        var accepted = true;
        bool? probe = null;
        coverage.Executed.Add(kind);
        try
        {
            switch (kind)
            {
                case GateOperationKind.BeginAcquisition:
                {
                    var acquisition = new OwnershipAcquisition<long>(1, 2);
                    _map.Add(Range, acquisition);
                    _gate = acquisition;
                    break;
                }
                case GateOperationKind.BeginRelease:
                {
                    var release = new OwnershipRelease<long>(1, 2);
                    _map.Add(Range, release);
                    _gate = release;
                    break;
                }
                case GateOperationKind.BeginBarrier:
                {
                    var barrier = new ViewBarrier<long>(2);
                    _map.Add(Range, barrier);
                    _gate = barrier;
                    break;
                }
                case GateOperationKind.BeginNonIncreasingView:
                    _ = new OwnershipAcquisition<long>(2, 1);
                    break;
                case GateOperationKind.Install when _gate is OwnershipAcquisition<long> acquisition:
                    acquisition.MarkStateInstalled();
                    break;
                case GateOperationKind.Fence when _gate is OwnershipAcquisition<long> acquisition:
                    acquisition.MarkFenced(new(ClusterServiceFencingMode.External, 42));
                    break;
                case GateOperationKind.Drain when _gate is OwnershipRelease<long> release:
                    release.MarkDrained();
                    break;
                case GateOperationKind.Retain when _gate is OwnershipRelease<long> release:
                    release.MarkStateRetained();
                    break;
                case GateOperationKind.Complete:
                    switch (_gate)
                    {
                        case OwnershipAcquisition<long> acquisition: acquisition.Complete(); break;
                        case OwnershipRelease<long> release: release.Complete(); break;
                        case ViewBarrier<long> barrier: barrier.Complete(); break;
                        default: throw new InvalidOperationException("No gate has begun.");
                    }

                    coverage.CompletedRoles.Add(Role);
                    _map.Prune();
                    break;
                case GateOperationKind.Fail:
                    GetGate().Fail(_failure);
                    coverage.FailedRoles.Add(Role);
                    break;
                case GateOperationKind.Abort:
                    var gate = GetGate();
                    coverage.AbortedFailedGate |= gate.Status is TransitionGateStatus.Failed;
                    gate.Abort();
                    _map.Prune();
                    break;
                case GateOperationKind.Prune:
                    _map.Prune();
                    break;
                case GateOperationKind.ProbeOlderOverlap:
                    probe = _map.IsBlocked(Range, 1);
                    break;
                case GateOperationKind.ProbeEqualOverlap:
                    probe = _map.IsBlocked(Range, 2);
                    break;
                case GateOperationKind.ProbeNewerOverlap:
                    probe = _map.IsBlocked(Range, 3);
                    break;
                case GateOperationKind.ProbeNewerDisjoint:
                    probe = _map.IsBlocked(Disjoint, 3);
                    break;
                default:
                    throw new InvalidOperationException("This role does not expose the requested operation.");
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

        if (!accepted)
        {
            coverage.Rejected.Add(kind);
        }

        var status = _gate?.Status ?? TransitionGateStatus.Completed;
        var phase = _gate switch
        {
            OwnershipAcquisition<long> acquisition => (int)acquisition.Phase,
            OwnershipRelease<long> release => (int)release.Phase,
            _ => 0
        };
        var taskState = _gate?.Completion switch
        {
            { IsFaulted: true } task => ReferenceEquals(_failure, Assert.Single(task.Exception!.InnerExceptions))
                ? GateTaskState.Failed
                : throw new InvalidOperationException("The original exception was replaced."),
            { IsCanceled: true } => GateTaskState.Canceled,
            { IsCompletedSuccessfully: true } => GateTaskState.Completed,
            null => GateTaskState.Completed,
            _ => GateTaskState.Pending
        };
        return new(accepted, (int)Role, (int)status, phase, _map.IsBlocked(Range, 2),
            _gate is OwnershipAcquisition<long> { Fence: not null }, (int)taskState, probe);
    }

    private TransitionGate<long> GetGate() => _gate ?? throw new InvalidOperationException("No gate has begun.");

    private GateModelRole Role => _gate switch
    {
        OwnershipAcquisition<long> => GateModelRole.Acquisition,
        OwnershipRelease<long> => GateModelRole.Release,
        ViewBarrier<long> => GateModelRole.Barrier,
        _ => GateModelRole.None
    };
}

internal static class GateModel
{
    public static bool CanApply(GateOperationKind kind, GateModelState state)
    {
        var role = (GateModelRole)state.Role;
        var status = (TransitionGateStatus)state.Status;
        var pending = role is not GateModelRole.None && status is TransitionGateStatus.Pending;
        return kind switch
        {
            GateOperationKind.BeginAcquisition or GateOperationKind.BeginRelease or GateOperationKind.BeginBarrier =>
                role is GateModelRole.None || status is TransitionGateStatus.Completed or TransitionGateStatus.Aborted,
            GateOperationKind.BeginNonIncreasingView => false,
            GateOperationKind.Install => pending && role is GateModelRole.Acquisition && state.Phase == (int)AcquisitionPhase.AwaitingState,
            GateOperationKind.Fence => pending && role is GateModelRole.Acquisition && state.Phase == (int)AcquisitionPhase.StateInstalled,
            GateOperationKind.Drain => pending && role is GateModelRole.Release && state.Phase == (int)ReleasePhase.Blocking,
            GateOperationKind.Retain => pending && role is GateModelRole.Release && state.Phase == (int)ReleasePhase.Drained,
            GateOperationKind.Complete => pending && (role is GateModelRole.Barrier
                || role is GateModelRole.Acquisition && state.Phase == (int)AcquisitionPhase.Fenced
                || role is GateModelRole.Release && state.Phase is (int)ReleasePhase.Drained or (int)ReleasePhase.StateRetained),
            GateOperationKind.Fail => pending,
            GateOperationKind.Abort => role is not GateModelRole.None,
            _ => true
        };
    }

    public static GateResponse Predict(GateOperationKind kind, GateModelState state)
    {
        var accepted = CanApply(kind, state);
        var role = state.Role;
        var status = state.Status;
        var phase = state.Phase;
        var fence = state.HasFence;
        var taskState = state.TaskState;
        bool? probe = null;
        if (accepted)
        {
            switch (kind)
            {
                case GateOperationKind.BeginAcquisition:
                case GateOperationKind.BeginRelease:
                case GateOperationKind.BeginBarrier:
                    role = (int)(kind switch
                    {
                        GateOperationKind.BeginAcquisition => GateModelRole.Acquisition,
                        GateOperationKind.BeginRelease => GateModelRole.Release,
                        _ => GateModelRole.Barrier
                    });
                    status = (int)TransitionGateStatus.Pending;
                    taskState = (int)GateTaskState.Pending;
                    phase = 0;
                    fence = false;
                    break;
                case GateOperationKind.Install:
                    phase = (int)AcquisitionPhase.StateInstalled;
                    break;
                case GateOperationKind.Fence:
                    phase = (int)AcquisitionPhase.Fenced;
                    fence = true;
                    break;
                case GateOperationKind.Drain:
                    phase = (int)ReleasePhase.Drained;
                    break;
                case GateOperationKind.Retain:
                    phase = (int)ReleasePhase.StateRetained;
                    break;
                case GateOperationKind.Complete:
                    status = (int)TransitionGateStatus.Completed;
                    taskState = (int)GateTaskState.Completed;
                    break;
                case GateOperationKind.Fail:
                    status = (int)TransitionGateStatus.Failed;
                    taskState = (int)GateTaskState.Failed;
                    break;
                case GateOperationKind.Abort:
                    if (status is (int)TransitionGateStatus.Pending or (int)TransitionGateStatus.Failed)
                    {
                        status = (int)TransitionGateStatus.Aborted;
                        if (taskState == (int)GateTaskState.Pending)
                        {
                            taskState = (int)GateTaskState.Canceled;
                        }
                    }

                    break;
                case GateOperationKind.ProbeOlderOverlap:
                case GateOperationKind.ProbeNewerDisjoint:
                    probe = false;
                    break;
                case GateOperationKind.ProbeEqualOverlap:
                case GateOperationKind.ProbeNewerOverlap:
                    probe = role != (int)GateModelRole.None
                        && status is (int)TransitionGateStatus.Pending or (int)TransitionGateStatus.Failed;
                    break;
            }
        }

        var blocked = role != (int)GateModelRole.None
            && status is (int)TransitionGateStatus.Pending or (int)TransitionGateStatus.Failed;
        return new(accepted, role, status, phase, blocked, fence, taskState, probe);
    }

    public static void Apply(GateOperationKind kind, GateModelState state)
    {
        var next = Predict(kind, state);
        state.Role = next.Role;
        state.Status = next.Status;
        state.Phase = next.Phase;
        state.HasFence = next.HasFence;
        state.TaskState = next.TaskState;
    }
}

internal enum GateOperationKind
{
    BeginAcquisition,
    BeginRelease,
    BeginBarrier,
    BeginNonIncreasingView,
    Install,
    Fence,
    Drain,
    Retain,
    Complete,
    Fail,
    Abort,
    Prune,
    ProbeOlderOverlap,
    ProbeEqualOverlap,
    ProbeNewerOverlap,
    ProbeNewerDisjoint
}

internal enum GateModelRole
{
    None,
    Acquisition,
    Release,
    Barrier
}

internal enum GateTaskState
{
    Pending,
    Completed,
    Failed,
    Canceled
}

internal sealed class GateExecutionCoverage
{
    public HashSet<GateOperationKind> Executed { get; } = [];
    public HashSet<GateOperationKind> Rejected { get; } = [];
    public HashSet<GateModelRole> CompletedRoles { get; } = [];
    public HashSet<GateModelRole> FailedRoles { get; } = [];
    public bool AbortedFailedGate { get; set; }
}

internal sealed record GateRequest(GateOperationKind Kind);

internal sealed record GateResponse(
    bool Accepted,
    int Role,
    int Status,
    int Phase,
    bool Blocked,
    bool HasFence,
    int TaskState,
    bool? ProbeBlocked);

[State]
internal partial class GateModelState : State
{
    public int Role { get; set; }
    public int Status { get; set; }
    public int Phase { get; set; }
    public bool HasFence { get; set; }
    public int TaskState { get; set; }

    public static GateModelState Create() => new()
    {
        Status = (int)TransitionGateStatus.Completed,
        TaskState = (int)GateTaskState.Completed
    };
}

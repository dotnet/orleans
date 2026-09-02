using Microsoft.Accordant;
using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using TestExtensions;
using Xunit;

namespace UnitTests.Placement;

[TestArea("Placement")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[Trait("Phase", "3")]
[Trait("FullyQualifiedName", "UnitTests.Placement.ClusterDirectoryAccordantTests")]
public sealed class ClusterDirectoryAccordantTests
{
    [Fact]
    public Task Accordant_DirectoryOwnershipStateMachine_CoversAllTransitions()
        => RunAccordant(
            DirectoryScenario.Ownership,
            OperationKind.Lookup,
            OperationKind.AcquireEast,
            OperationKind.Renew,
            OperationKind.RelocateWest,
            OperationKind.AdvanceToLeaseBoundary,
            OperationKind.AdvancePastExpiry,
            OperationKind.Validate,
            OperationKind.StaleRenew,
            OperationKind.StaleRelocate);

    [Fact]
    public Task Accordant_DirectoryFencingStateMachine_RejectsEveryStaleMutation()
        => RunAccordant(
            DirectoryScenario.Fencing,
            OperationKind.AcquireEast,
            OperationKind.RelocateWest,
            OperationKind.AdvancePastExpiry,
            OperationKind.AcquireEast,
            OperationKind.AdvanceTopologyEpoch,
            OperationKind.StaleRenew,
            OperationKind.StaleRelocate,
            OperationKind.Validate);

    [Fact]
    public Task Accordant_TopologyStateMachine_CoversActiveDrainingRemovedTransitions()
        => RunAccordant(
            DirectoryScenario.Topology,
            OperationKind.AcquireEast,
            OperationKind.DrainEast,
            OperationKind.RemoveEast,
            OperationKind.RemoveWest,
            OperationKind.AddWest,
            OperationKind.AdvanceTopologyEpoch,
            OperationKind.Renew,
            OperationKind.RelocateWest,
            OperationKind.Validate);

    [Fact]
    public Task Accordant_DirectoryAndTopologyCombinedModel_PreservesSingleOwnerAndMonotonicFence()
        => RunAccordant(
            DirectoryScenario.Combined,
            Enum.GetValues<OperationKind>());

    private static async Task RunAccordant(DirectoryScenario scenario, params OperationKind[] operationKinds)
    {
        var spec = new DirectoryBehavioralSpec();
        var initialState = DirectoryModelState.Create();
        var inputSet = spec.CreateInputSet(operationKinds);
        var testCases = spec.GenerateTests(
                initialState,
                inputSet,
                new TestGenerationOptions
                {
                    MaxDepth = 3,
                    SequentialTestCaseAlgorithm = SequentialTestCaseAlgorithms.CreateTransitionCoverage(maxSequenceLength: 4),
                    ShouldApply = (input, state) => DirectoryBehavioralSpec.CanApply(
                        (DirectoryRequest)input.Request,
                        (DirectoryModelState)state)
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
                BeforeEach = info => info.Context.Register(new DirectoryExecutionHarness()),
                AfterEach = info =>
                {
                    var harness = info.Context.Get<DirectoryExecutionHarness>();
                    harness.Dispose();
                }
            });
        var failure = results.FirstOrDefault(result => !result.Success);

        Assert.NotEmpty(testCases);
        Assert.True(
            failure is null && results.All(result => result.Success),
            $"seed=phase3-{scenario}; cases={testCases.Count}; failure={failure?.LastFailureMessage}");
    }

    private enum DirectoryScenario
    {
        Ownership,
        Fencing,
        Topology,
        Combined
    }

    private sealed class DirectoryBehavioralSpec : Spec<DirectoryModelState>
    {
        private readonly DirectoryOperation _operation = new();

        public DirectoryBehavioralSpec() => Add(_operation);

        public InputSet CreateInputSet(IEnumerable<OperationKind> kinds)
        {
            var result = new InputSet();
            foreach (var kind in kinds.Distinct())
            {
                result.Add(_operation.With(new DirectoryRequest(kind), kind.ToString()));
            }

            return result;
        }

        public static bool CanApply(DirectoryRequest request, DirectoryModelState state)
        {
            var live = state.Current is not null && state.Current.LeaseExpirationTicks > state.NowTicks;
            return request.Kind switch
            {
                OperationKind.AcquireEast => !live && state.EastState == (int)MetaclusterClusterState.Active,
                OperationKind.AcquireWest => !live && state.WestState == (int)MetaclusterClusterState.Active,
                OperationKind.Renew => live && OwnerState(state) != MetaclusterClusterState.Removed,
                OperationKind.RelocateEast =>
                    state.Current is not null
                    && !live
                    && state.EastState == (int)MetaclusterClusterState.Active,
                OperationKind.RelocateWest =>
                    state.Current is not null
                    && !live
                    && state.WestState == (int)MetaclusterClusterState.Active,
                OperationKind.StaleRenew or OperationKind.StaleRelocate =>
                    state.Prior is not null
                    && state.Current is not null
                    && state.Prior.Version != state.Current.Version,
                OperationKind.DrainEast => state.EastState == (int)MetaclusterClusterState.Active,
                OperationKind.RemoveEast => state.EastState != (int)MetaclusterClusterState.Removed,
                OperationKind.RemoveWest => state.WestState != (int)MetaclusterClusterState.Removed,
                OperationKind.AddWest => state.WestState == (int)MetaclusterClusterState.Removed,
                _ => true
            };
        }

        private static MetaclusterClusterState OwnerState(DirectoryModelState state)
            => state.Current?.ClusterId switch
            {
                "east" => (MetaclusterClusterState)state.EastState,
                "west" => (MetaclusterClusterState)state.WestState,
                _ => MetaclusterClusterState.Removed
            };
    }

    private sealed class DirectoryOperation()
        : Operation<DirectoryRequest, DirectoryResponse, DirectoryModelState>("DirectoryOperation")
    {
        public override ExpectedOutcomes Apply(DirectoryRequest request, DirectoryModelState state)
        {
            var expected = Predict(request, state);
            return Expect.That(response => Validate(request, expected, response))
                .ThenState(next => ApplyModel(request, next));
        }

        public override Task<DirectoryResponse> ExecuteAsync(TestingContext context, DirectoryRequest request)
            => context.Get<DirectoryExecutionHarness>().Execute(request);

        private static ValidationResult Validate(
            DirectoryRequest request,
            DirectoryResponse expected,
            DirectoryResponse actual)
        {
            if (expected.Succeeded == actual.Succeeded
                && expected.TopologyEpoch == actual.TopologyEpoch
                && expected.EastState == actual.EastState
                && expected.WestState == actual.WestState
                && EntriesEqual(expected.Entry, actual.Entry))
            {
                return ValidationResult.Valid();
            }

            return ValidationResult.Invalid(
                $"request={request}; expected={expected}; actual={actual}");

            static bool EntriesEqual(ModelEntry? expectedEntry, ModelEntry? actualEntry)
                => ReferenceEquals(expectedEntry, actualEntry)
                    || (expectedEntry is not null
                        && actualEntry is not null
                        && expectedEntry.ClusterId == actualEntry.ClusterId
                        && expectedEntry.Version == actualEntry.Version
                        && expectedEntry.TopologyEpoch == actualEntry.TopologyEpoch
                        && expectedEntry.FencingToken == actualEntry.FencingToken
                        && expectedEntry.LeaseExpirationTicks == actualEntry.LeaseExpirationTicks);
        }
    }

    private sealed class DirectoryExecutionHarness : IDisposable
    {
        private static readonly GrainId Grain = GrainId.Create("accordant.directory", "grain-1");
        private static readonly DateTimeOffset Start = new(2038, 4, 5, 6, 7, 8, TimeSpan.Zero);
        private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly FakeTimeProvider _clock = new(Start);
        private readonly InMemoryClusterDirectory _directory;
        private ModelEntry? _current;
        private ModelEntry? _prior;
        private int _eastState = (int)MetaclusterClusterState.Active;
        private int _westState = (int)MetaclusterClusterState.Active;
        private long _epoch = 1;

        public DirectoryExecutionHarness() => _directory = new InMemoryClusterDirectory(_clock);

        public async Task<DirectoryResponse> Execute(DirectoryRequest request)
        {
            var success = true;
            switch (request.Kind)
            {
                case OperationKind.Lookup:
                case OperationKind.Validate:
                    break;
                case OperationKind.AcquireEast:
                    await Acquire("east");
                    break;
                case OperationKind.AcquireWest:
                    await Acquire("west");
                    break;
                case OperationKind.Renew:
                    var renewed = await _directory.TryRenew(Grain, _current!.Version, _current.ClusterId, Lease, _cancellation.Token);
                    success = renewed is not null;
                    if (renewed is not null)
                    {
                        _current = ModelEntry.From(renewed);
                    }

                    break;
                case OperationKind.RelocateEast:
                    await Relocate("east");
                    break;
                case OperationKind.RelocateWest:
                    await Relocate("west");
                    break;
                case OperationKind.AdvanceToLeaseBoundary:
                    _clock.Advance(Lease);
                    break;
                case OperationKind.AdvancePastExpiry:
                    _clock.Advance(Lease + TimeSpan.FromTicks(1));
                    break;
                case OperationKind.StaleRenew:
                    success = await _directory.TryRenew(
                        Grain,
                        _prior!.Version,
                        _prior.ClusterId,
                        Lease,
                        _cancellation.Token) is not null;
                    break;
                case OperationKind.StaleRelocate:
                    success = await _directory.TryMove(
                        Grain,
                        _prior!.Version,
                        "east",
                        _epoch,
                        Lease,
                        _cancellation.Token) is not null;
                    break;
                case OperationKind.DrainEast:
                    _eastState = (int)MetaclusterClusterState.Draining;
                    _epoch++;
                    break;
                case OperationKind.RemoveEast:
                    _eastState = (int)MetaclusterClusterState.Removed;
                    _epoch++;
                    break;
                case OperationKind.RemoveWest:
                    _westState = (int)MetaclusterClusterState.Removed;
                    _epoch++;
                    break;
                case OperationKind.AddWest:
                    _westState = (int)MetaclusterClusterState.Active;
                    _epoch++;
                    break;
                case OperationKind.AdvanceTopologyEpoch:
                    _epoch++;
                    break;
            }

            var live = await _directory.Lookup(Grain, _cancellation.Token);
            return new DirectoryResponse(
                success,
                live is null ? null : ModelEntry.From(live),
                _epoch,
                _eastState,
                _westState);
        }

        public void Dispose() => _cancellation.Dispose();

        private async Task Acquire(string clusterId)
        {
            var acquired = await _directory.GetOrCreate(Grain, clusterId, _epoch, Lease, _cancellation.Token);
            if (_current is not null && _current.Version != acquired.Version)
            {
                _prior = _current;
            }

            _current = ModelEntry.From(acquired);
        }

        private async Task Relocate(string clusterId)
        {
            var moved = await _directory.TryMove(
                Grain,
                _current!.Version,
                clusterId,
                _epoch,
                Lease,
                _cancellation.Token);
            if (moved is not null)
            {
                _prior = _current;
                _current = ModelEntry.From(moved);
            }
        }
    }

    private static DirectoryResponse Predict(DirectoryRequest request, DirectoryModelState state)
    {
        var success = request.Kind is not (OperationKind.StaleRenew or OperationKind.StaleRelocate);
        var now = state.NowTicks;
        var epoch = state.TopologyEpoch;
        var eastState = state.EastState;
        var westState = state.WestState;
        var current = state.Current is null ? null : Copy(state.Current);
        switch (request.Kind)
        {
            case OperationKind.AcquireEast:
                current = Acquire("east");
                break;
            case OperationKind.AcquireWest:
                current = Acquire("west");
                break;
            case OperationKind.Renew:
                current!.LeaseExpirationTicks = now + TimeSpan.FromMinutes(5).Ticks;
                break;
            case OperationKind.RelocateEast:
                current = Relocate("east");
                break;
            case OperationKind.RelocateWest:
                current = Relocate("west");
                break;
            case OperationKind.AdvanceToLeaseBoundary:
                now += TimeSpan.FromMinutes(5).Ticks;
                break;
            case OperationKind.AdvancePastExpiry:
                now += TimeSpan.FromMinutes(5).Ticks + 1;
                break;
            case OperationKind.DrainEast:
                eastState = (int)MetaclusterClusterState.Draining;
                epoch++;
                break;
            case OperationKind.RemoveEast:
                eastState = (int)MetaclusterClusterState.Removed;
                epoch++;
                break;
            case OperationKind.RemoveWest:
                westState = (int)MetaclusterClusterState.Removed;
                epoch++;
                break;
            case OperationKind.AddWest:
                westState = (int)MetaclusterClusterState.Active;
                epoch++;
                break;
            case OperationKind.AdvanceTopologyEpoch:
                epoch++;
                break;
        }

        return new DirectoryResponse(
            success,
            current is not null && current.LeaseExpirationTicks > now ? current : null,
            epoch,
            eastState,
            westState);

        ModelEntry Acquire(string cluster)
        {
            var next = state.NextVersion + 1;
            return new ModelEntry
            {
                ClusterId = cluster,
                Version = next,
                TopologyEpoch = epoch,
                FencingToken = next,
                LeaseExpirationTicks = now + TimeSpan.FromMinutes(5).Ticks
            };
        }

        ModelEntry Relocate(string cluster)
        {
            var next = state.NextVersion + 1;
            return new ModelEntry
            {
                ClusterId = cluster,
                Version = next,
                TopologyEpoch = epoch,
                FencingToken = next,
                LeaseExpirationTicks = now + TimeSpan.FromMinutes(5).Ticks
            };
        }
    }

    private static void ApplyModel(DirectoryRequest request, DirectoryModelState state)
    {
        switch (request.Kind)
        {
            case OperationKind.AcquireEast:
                Acquire("east");
                break;
            case OperationKind.AcquireWest:
                Acquire("west");
                break;
            case OperationKind.Renew:
                state.Current!.LeaseExpirationTicks = state.NowTicks + TimeSpan.FromMinutes(5).Ticks;
                break;
            case OperationKind.RelocateEast:
                Relocate("east");
                break;
            case OperationKind.RelocateWest:
                Relocate("west");
                break;
            case OperationKind.AdvanceToLeaseBoundary:
                state.NowTicks += TimeSpan.FromMinutes(5).Ticks;
                break;
            case OperationKind.AdvancePastExpiry:
                state.NowTicks += TimeSpan.FromMinutes(5).Ticks + 1;
                break;
            case OperationKind.DrainEast:
                state.EastState = (int)MetaclusterClusterState.Draining;
                state.TopologyEpoch++;
                break;
            case OperationKind.RemoveEast:
                state.EastState = (int)MetaclusterClusterState.Removed;
                state.TopologyEpoch++;
                break;
            case OperationKind.RemoveWest:
                state.WestState = (int)MetaclusterClusterState.Removed;
                state.TopologyEpoch++;
                break;
            case OperationKind.AddWest:
                state.WestState = (int)MetaclusterClusterState.Active;
                state.TopologyEpoch++;
                break;
            case OperationKind.AdvanceTopologyEpoch:
                state.TopologyEpoch++;
                break;
        }

        void Acquire(string cluster)
        {
            if (state.Current is not null)
            {
                state.Prior = Copy(state.Current);
            }

            state.NextVersion++;
            state.Current = new ModelEntry
            {
                ClusterId = cluster,
                Version = state.NextVersion,
                TopologyEpoch = state.TopologyEpoch,
                FencingToken = state.NextVersion,
                LeaseExpirationTicks = state.NowTicks + TimeSpan.FromMinutes(5).Ticks
            };
        }

        void Relocate(string cluster)
        {
            state.Prior = Copy(state.Current!);
            state.NextVersion++;
            state.Current = new ModelEntry
            {
                ClusterId = cluster,
                Version = state.NextVersion,
                TopologyEpoch = state.TopologyEpoch,
                FencingToken = state.NextVersion,
                LeaseExpirationTicks = state.NowTicks + TimeSpan.FromMinutes(5).Ticks
            };
        }
    }

    private static ModelEntry Copy(ModelEntry entry)
        => new()
        {
            ClusterId = entry.ClusterId,
            Version = entry.Version,
            TopologyEpoch = entry.TopologyEpoch,
            FencingToken = entry.FencingToken,
            LeaseExpirationTicks = entry.LeaseExpirationTicks
        };

    private enum OperationKind
    {
        Lookup,
        AcquireEast,
        AcquireWest,
        Renew,
        RelocateEast,
        RelocateWest,
        AdvanceToLeaseBoundary,
        AdvancePastExpiry,
        Validate,
        AddWest,
        DrainEast,
        RemoveEast,
        RemoveWest,
        AdvanceTopologyEpoch,
        StaleRenew,
        StaleRelocate
    }

    private sealed record DirectoryRequest(OperationKind Kind)
    {
        public override string ToString() => Kind.ToString();
    }

    private sealed record DirectoryResponse(
        bool Succeeded,
        ModelEntry? Entry,
        long TopologyEpoch,
        int EastState,
        int WestState);

    [Fact]
    public Task Accordant_DirectoryReacquisitionAndStaleFencing_ExploresDeepSequences()
        => RunAccordantAtDepth(
            DirectoryScenario.Combined,
            OperationKind.AcquireEast,
            OperationKind.AdvancePastExpiry,
            OperationKind.RelocateWest,
            OperationKind.StaleRenew,
            OperationKind.StaleRelocate);

    private static async Task RunAccordantAtDepth(
        DirectoryScenario scenario,
        params OperationKind[] operationKinds)
    {
        var spec = new DirectoryBehavioralSpec();
        var initialState = DirectoryModelState.Create();
        var testCases = spec.GenerateTests(
                initialState,
                spec.CreateInputSet(operationKinds),
                new TestGenerationOptions
                {
                    MaxDepth = 4,
                    SequentialTestCaseAlgorithm = SequentialTestCaseAlgorithms.CreateTransitionCoverage(maxSequenceLength: 4),
                    ShouldApply = (input, state) => DirectoryBehavioralSpec.CanApply(
                        (DirectoryRequest)input.Request,
                        (DirectoryModelState)state)
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
                BeforeEach = info => info.Context.Register(new DirectoryExecutionHarness()),
                AfterEach = info => info.Context.Get<DirectoryExecutionHarness>().Dispose()
            });
        var failure = results.FirstOrDefault(result => !result.Success);

        Assert.NotEmpty(testCases);
        Assert.True(
            failure is null && results.All(result => result.Success),
            $"seed=phase3-depth-{scenario}; cases={testCases.Count}; "
            + $"failure={failure?.LastFailureMessage}; log={failure?.LogFilePath}");
    }
}

[State]
internal partial class DirectoryModelState : State
{
    public ModelEntry? Current { get; set; }

    public ModelEntry? Prior { get; set; }

    public long NextVersion { get; set; }

    public long NowTicks { get; set; }

    public long TopologyEpoch { get; set; }

    public int EastState { get; set; }

    public int WestState { get; set; }

    public static DirectoryModelState Create()
        => new()
        {
            NowTicks = new DateTimeOffset(2038, 4, 5, 6, 7, 8, TimeSpan.Zero).Ticks,
            TopologyEpoch = 1,
            EastState = (int)MetaclusterClusterState.Active,
            WestState = (int)MetaclusterClusterState.Active
        };
}

[State]
internal partial class ModelEntry : State
{
    public string ClusterId { get; set; } = string.Empty;

    public long Version { get; set; }

    public long TopologyEpoch { get; set; }

    public long FencingToken { get; set; }

    public long LeaseExpirationTicks { get; set; }

    public static ModelEntry From(ClusterDirectoryEntry entry)
        => new()
        {
            ClusterId = entry.ClusterId,
            Version = entry.Version,
            TopologyEpoch = entry.TopologyEpoch,
            FencingToken = entry.FencingToken,
            LeaseExpirationTicks = entry.LeaseExpiration.UtcTicks
        };
}

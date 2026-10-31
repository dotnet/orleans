using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Microsoft.Accordant;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Diagnostics;
using Orleans.GrainDirectory;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Scheduler;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

/// <summary>
/// Verifies the activation state machine: each setup phase is mirrored by the teardown phase which undoes it, teardown starts at the
/// mirror of the last completed setup phase, and a deactivation request during setup is recorded intent rather than a state.
/// </summary>
[TestSuite("BVT"), TestProvider("None"), TestCategory("BVT")]
public class ActivationStateMachineTests(ActivationStateMachineTests.Fixture fixture) : IClassFixture<ActivationStateMachineTests.Fixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void TeardownEntry_IsMirrorOfLastCompletedSetupPhase()
    {
        // Disposing undoes the activation context as well as construction.
        (ActivationState LastCompleted, ActivationState Entry)[] expected =
        [
            (ActivationState.None, ActivationState.Disposing),
            (ActivationState.Constructing, ActivationState.Disposing),
            (ActivationState.Registering, ActivationState.Unregistering),
            (ActivationState.Rehydrating, ActivationState.Dehydrating),
            (ActivationState.Starting, ActivationState.Stopping),
            (ActivationState.Activating, ActivationState.Deactivating),
        ];

        foreach (var (lastCompleted, entry) in expected)
        {
            Assert.Equal(entry, lastCompleted.GetTeardownEntry());
            Assert.True(ActivationStateExtensions.IsValidTransition(lastCompleted, entry));
        }
    }

    [Fact]
    public void FailureTeardownEntry_UndoesCompletedPhasesAndPartialStart()
    {
        // A failed phase leaves the phase before it as the last completed phase. Lifecycle stop undoes a partial start.
        (ActivationState Failed, ActivationState Entry)[] expected =
        [
            (ActivationState.Constructing, ActivationState.Disposing),
            (ActivationState.Registering, ActivationState.Disposing),
            (ActivationState.Rehydrating, ActivationState.Unregistering),
            (ActivationState.Starting, ActivationState.Stopping),
            (ActivationState.Activating, ActivationState.Stopping),
        ];

        foreach (var (failed, entry) in expected)
        {
            Assert.Equal(entry, failed.GetFailureTeardownEntry());
            Assert.True(ActivationStateExtensions.IsValidTransition(failed, entry));
        }
    }

    [Fact]
    public void Transitions_AdvanceOneStepOrEnterTeardownAtAMirror()
    {
        HashSet<(ActivationState, ActivationState)> expected =
        [
            // Each phase advances to the next one.
            (ActivationState.None, ActivationState.Constructing),
            (ActivationState.Constructing, ActivationState.Registering),
            (ActivationState.Registering, ActivationState.Rehydrating),
            (ActivationState.Rehydrating, ActivationState.Starting),
            (ActivationState.Starting, ActivationState.Activating),
            (ActivationState.Activating, ActivationState.Active),
            (ActivationState.Active, ActivationState.Deactivating),
            (ActivationState.Deactivating, ActivationState.Stopping),
            (ActivationState.Stopping, ActivationState.Dehydrating),
            (ActivationState.Dehydrating, ActivationState.Unregistering),
            (ActivationState.Unregistering, ActivationState.Disposing),
            (ActivationState.Disposing, ActivationState.Destroyed),

            // Setup stops after a completed phase at its mirror, or after a failed phase at the mirror of the phase before it.
            // Disposing undoes the activation context and construction, and lifecycle stop undoes a partial start.
            (ActivationState.None, ActivationState.Disposing),
            (ActivationState.Constructing, ActivationState.Disposing),
            (ActivationState.Registering, ActivationState.Unregistering),
            (ActivationState.Registering, ActivationState.Disposing),
            (ActivationState.Rehydrating, ActivationState.Dehydrating),
            (ActivationState.Rehydrating, ActivationState.Unregistering),
            (ActivationState.Starting, ActivationState.Stopping),
            (ActivationState.Activating, ActivationState.Deactivating),
            (ActivationState.Activating, ActivationState.Stopping),
        ];

        foreach (var from in Enum.GetValues<ActivationState>())
        {
            foreach (var to in Enum.GetValues<ActivationState>())
            {
                Assert.True(
                    expected.Contains((from, to)) == ActivationStateExtensions.IsValidTransition(from, to),
                    $"{from} -> {to}: expected valid={expected.Contains((from, to))}");
            }
        }
    }

    /// <summary>
    /// Drives real activations through generated sequences of phase completions, phase failures, deactivation requests, migration requests,
    /// and forced disposal, comparing each observed phase, state, and migration outcome with the state machine model.
    /// </summary>
    [Fact]
    public async Task Lifecycle_ConformsToStateMachineModel()
    {
        var spec = new ActivationLifecycleSpec();
        var initial = new ActivationLifecycleModelState();
        var cases = spec.GenerateTests(
            initial,
            spec.CreateInputSet(),
            new TestGenerationOptions
            {
                MaxDepth = 10,
                SequentialTestCaseAlgorithm = SequentialTestCaseAlgorithms.CreateTransitionCoverage(maxSequenceLength: 10),
                ShouldApply = (input, state) => ActivationLifecycleModel.CanApply(((ActivationLifecycleRequest)input.Request).Kind, (ActivationLifecycleModelState)state),
            });
        Assert.NotEmpty(cases);

        var context = spec.CreateTestingContext();
        context.RequestPrinter = request => request?.ToString() ?? "<null>";
        context.ResponsePrinter = response => response?.ToString() ?? "<null>";
        var observed = new ConcurrentDictionary<ActivationState, bool>();
        ActivationLifecycleExecutor? current = null;
        var results = await spec.RunTests(
            context,
            initial,
            cases,
            new TestExecutionOptions
            {
                StopOnFirstFailure = true,
                BeforeEach = info =>
                {
                    current = new ActivationLifecycleExecutor(fixture, observed);
                    info.Context.Register(current);
                },
                AfterEachAsync = async info =>
                {
                    if (!info.Success)
                    {
                        TestContext.Current.TestOutputHelper?.WriteLine(info.FailureMessage);
                    }

                    if (current is { } executor)
                    {
                        current = null;
                        try
                        {
                            executor.AssertQuiescent();
                        }
                        finally
                        {
                            await executor.DisposeAsync();
                        }
                    }
                },
            }).WaitAsync(TimeSpan.FromMinutes(5), Cancellation);

        var failure = results.FirstOrDefault(result => !result.Success);
        Assert.True(failure is null, failure?.LastFailureMessage);

        // Every phase which the gated grain exposes was reached by some generated case.
        Assert.Equal(
            [
                ActivationState.Registering, ActivationState.Starting, ActivationState.Activating, ActivationState.Active,
                ActivationState.Deactivating, ActivationState.Stopping, ActivationState.Unregistering, ActivationState.Destroyed,
            ],
            observed.Keys.Order());
    }

    [Fact]
    public async Task ForcedDisposal_DuringSetup_EntersDisposingAndSkipsRemainingSetup()
    {
        await using var executor = new ActivationLifecycleExecutor(fixture);
        await executor.Execute(ActivationLifecycleOperation.Activate);
        await executor.Execute(ActivationLifecycleOperation.Release);
        await executor.Execute(ActivationLifecycleOperation.Release);
        var activation = executor.Activation!;
        Assert.Equal(ActivationState.Activating, activation.State);

        await activation.DisposeAsync().AsTask().WaitAsync(Timeout, Cancellation);
        Assert.Equal(ActivationState.Destroyed, activation.State);

        // Forced disposal disposes resources only. Its caller removes the activation from the catalog, as ActivationDirectory disposal does.
        Assert.False(activation.Deactivated.IsCompleted);
        fixture.Catalog.UnregisterMessageTarget(activation);
        Assert.False(fixture.HostedCluster.TryGetGrainContext(activation.GrainId, out _));

        // OnActivateAsync returns into a disposed activation, which neither becomes active nor stops its lifecycle.
        // Later callbacks are recorded without waiting, so a wrongly run lifecycle stop is observed rather than blocked.
        executor.Gate.ReleaseAll();
        await executor.DrainActivationScheduler();
        Assert.Equal(ActivationState.Destroyed, activation.State);
        executor.AssertQuiescent();
        Assert.False(activation.IsValid);
    }

    [Fact]
    public async Task ForcedDisposal_DuringTeardown_EntersDisposingAndSkipsRemainingTeardown()
    {
        await using var executor = new ActivationLifecycleExecutor(fixture);
        await executor.Execute(ActivationLifecycleOperation.Activate);
        await executor.Execute(ActivationLifecycleOperation.Release);
        await executor.Execute(ActivationLifecycleOperation.Release);
        await executor.Execute(ActivationLifecycleOperation.Release);
        await executor.Execute(ActivationLifecycleOperation.Deactivate);
        var activation = executor.Activation!;
        Assert.Equal(ActivationState.Deactivating, activation.State);

        await activation.DisposeAsync().AsTask().WaitAsync(Timeout, Cancellation);
        Assert.Equal(ActivationState.Destroyed, activation.State);

        // Teardown resumes after OnDeactivateAsync, skips the lifecycle stop and unregistration which forced disposal preempted, and completes deactivation.
        // Later callbacks are recorded without waiting, so a wrongly run lifecycle stop is observed rather than blocked.
        executor.Gate.ReleaseAll();
        await activation.Deactivated.WaitAsync(Timeout, Cancellation);
        Assert.Equal(ActivationState.Destroyed, activation.State);
        Assert.Equal(ActivationState.Destroyed, await executor.Gate.ReadObservation());
        executor.AssertQuiescent();
        Assert.False(fixture.HostedCluster.TryGetGrainContext(activation.GrainId, out _));
    }

    /// <summary>
    /// Lifecycle observers run synchronously inside <c>Deactivate</c>. One which forces disposal there preempts teardown, and deactivation
    /// still completes.
    /// </summary>
    [Fact]
    public async Task ForcedDisposal_FromDeactivatingObserver_CompletesDeactivation()
    {
        await using var executor = new ActivationLifecycleExecutor(fixture);
        await executor.Execute(ActivationLifecycleOperation.Activate);
        await executor.Execute(ActivationLifecycleOperation.Release);
        await executor.Execute(ActivationLifecycleOperation.Release);
        await executor.Execute(ActivationLifecycleOperation.Release);
        var activation = executor.Activation!;
        Assert.Equal(ActivationState.Active, activation.State);

        Task? disposal = null;
        using (GrainLifecycleEvents.AllEvents.Subscribe(new ForcedDisposalObserver(activation, task => disposal = task)))
        {
            activation.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Disposed while deactivating."), Cancellation);
        }

        Assert.NotNull(disposal);
        await disposal.WaitAsync(Timeout, Cancellation);
        await activation.Deactivated.WaitAsync(Timeout, Cancellation);
        Assert.Equal(ActivationState.Destroyed, activation.State);
        Assert.Equal(ActivationState.Destroyed, await executor.Gate.ReadObservation());
        executor.AssertQuiescent();
        Assert.False(fixture.HostedCluster.TryGetGrainContext(activation.GrainId, out _));
    }

    private sealed class ForcedDisposalObserver(ActivationData target, Action<Task> onDisposal) : IObserver<GrainLifecycleEvents.LifecycleEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(GrainLifecycleEvents.LifecycleEvent value)
        {
            if (value is GrainLifecycleEvents.Deactivating && ReferenceEquals(value.GrainContext, target))
            {
                onDisposal(target.DisposeAsync().AsTask());
            }
        }
    }

    /// <summary>
    /// A deactivation request which arrives after <c>OnActivateAsync</c> returns and passes its cancellation check, but before the activation
    /// becomes active, finds activation completed. Teardown therefore starts at Deactivating and runs <c>OnDeactivateAsync</c>.
    /// </summary>
    [Fact]
    public async Task DeactivationAfterActivationCompletes_RunsOnDeactivateAsync()
    {
        await using var executor = new ActivationLifecycleExecutor(fixture);
        var grainId = executor.GrainId.ToString();

        // The OnActivateAsync span stops between OnActivateAsync's cancellation check and the Active boundary.
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == ActivitySources.LifecycleActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == ActivityNames.OnActivate && Equals(activity.GetTagItem(ActivityTagKeys.GrainId), grainId))
                {
                    executor.Activation!.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Deactivated once activation completed."));
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        Assert.Equal(new ActivationLifecycleObservation(ActivationState.Registering, ActivationState.Registering, false), await executor.Execute(ActivationLifecycleOperation.Activate));
        Assert.Equal(new ActivationLifecycleObservation(ActivationState.Starting, ActivationState.Starting, false), await executor.Execute(ActivationLifecycleOperation.Release));
        Assert.Equal(new ActivationLifecycleObservation(ActivationState.Activating, ActivationState.Activating, false), await executor.Execute(ActivationLifecycleOperation.Release));
        Assert.Equal(new ActivationLifecycleObservation(ActivationState.Deactivating, ActivationState.Deactivating, true), await executor.Execute(ActivationLifecycleOperation.Release));
        Assert.Equal(new ActivationLifecycleObservation(ActivationState.Stopping, ActivationState.Stopping, true), await executor.Execute(ActivationLifecycleOperation.Release));
        Assert.Equal(new ActivationLifecycleObservation(ActivationState.Unregistering, ActivationState.Unregistering, true), await executor.Execute(ActivationLifecycleOperation.Release));
        Assert.Equal(new ActivationLifecycleObservation(ActivationState.Destroyed, ActivationState.Destroyed, true), await executor.Execute(ActivationLifecycleOperation.Release));
        executor.AssertQuiescent();
    }

    public sealed class Fixture : BaseInProcessTestClusterFixture
    {
        public PhaseGateRegistry Gates => HostedCluster.Silos[0].ServiceProvider.GetRequiredService<PhaseGateRegistry>();
        internal Catalog Catalog => HostedCluster.Silos[0].ServiceProvider.GetRequiredService<Catalog>();

        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            ActivationAssertionRecorder.Install();
            builder.Options.InitialSilosCount = 1;
            builder.ConfigureSilo((_, silo) =>
            {
                silo.Services.AddSingleton<PhaseGateRegistry>();
                silo.Services.AddSingleton<PhaseGateDirectory>();
                silo.Services.AddSingleton<IGrainDirectoryResolver, PhaseGateDirectoryResolver>();
            });
        }

        public override async ValueTask DisposeAsync()
        {
            Gates.ReleaseAll();
            await base.DisposeAsync();
            ActivationAssertionRecorder.AssertNone();
        }
    }

    internal sealed class ActivationLifecycleExecutor : IAsyncDisposable
    {
        private readonly Fixture _fixture;
        private readonly ConcurrentDictionary<ActivationState, bool>? _observed;
        private readonly List<string> _history = [];
        private ActivationState? _phase;

        public ActivationLifecycleExecutor(Fixture fixture, ConcurrentDictionary<ActivationState, bool>? observed = null)
        {
            _fixture = fixture;
            _observed = observed;
            GrainId = fixture.GrainFactory.GetGrain<IPhaseGateGrain>(Guid.NewGuid().ToString("N")).GetGrainId();
            Gate = fixture.Gates.Track(GrainId);
        }

        public GrainId GrainId { get; }
        public PhaseGate Gate { get; }
        public ActivationData? Activation { get; private set; }

        public async Task<ActivationLifecycleObservation> Execute(ActivationLifecycleOperation operation)
        {
            _history.Add(operation.ToString());
            bool? migrationAccepted = null;
            try
            {
                switch (operation)
                {
                    case ActivationLifecycleOperation.Activate:
                        Activation = Assert.IsType<ActivationData>(_fixture.Catalog.GetOrCreateActivation(GrainId, requestContextData: null));
                        Gate.ObserveDeactivation(Activation);
                        _phase = await Gate.ReadObservation();
                        break;
                    case ActivationLifecycleOperation.Release:
                        Gate.Release();
                        _phase = await Gate.ReadObservation();
                        break;
                    case ActivationLifecycleOperation.Fail:
                        Gate.Fail();
                        _phase = await Gate.ReadObservation();
                        break;
                    case ActivationLifecycleOperation.Deactivate:
                        var wasActive = Activation!.State is ActivationState.Active;
                        Activation.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Activation state machine test."), Cancellation);

                        // Only an active activation changes state. During setup, the request is recorded intent.
                        if (wasActive)
                        {
                            _phase = await Gate.ReadObservation();
                        }

                        break;
                    case ActivationLifecycleOperation.Migrate:
                        var wasActiveForMigration = Activation!.State is ActivationState.Active;
                        migrationAccepted = Activation.TryStartMigration(requestContext: null, Cancellation);

                        // An accepted request deactivates the activation, which an active activation does immediately.
                        if (migrationAccepted is true && wasActiveForMigration)
                        {
                            _phase = await Gate.ReadObservation();
                        }

                        break;
                    case ActivationLifecycleOperation.Dispose:
                        await Activation!.DisposeAsync().AsTask().WaitAsync(Timeout, Cancellation);
                        if (Activation.IsDeactivationRequested)
                        {
                            // The deactivation operation resumes once its pending callback completes. Later callbacks are recorded without
                            // waiting, so a phase which forced disposal should have preempted is observed before the activation's destruction.
                            Gate.ReleaseAll();
                            await Activation.Deactivated.WaitAsync(Timeout, Cancellation);
                            _phase = await Gate.ReadObservation();
                        }
                        else
                        {
                            // Forced disposal disposes resources only. Its caller removes the activation from the catalog, as ActivationDirectory disposal does.
                            _fixture.Catalog.UnregisterMessageTarget(Activation);
                            _phase = Activation.State;
                        }

                        break;
                }

                var result = new ActivationLifecycleObservation(_phase, Activation!.State, Activation.IsDeactivationRequested, migrationAccepted);
                _history[^1] += $" => {result}";
                if (_phase is { } phase)
                {
                    _observed?.TryAdd(phase, true);
                }

                return result;
            }
            catch (Exception exception)
            {
                return new ActivationLifecycleObservation(_phase, Activation?.State, Activation?.IsDeactivationRequested ?? false, migrationAccepted,
                    $"grain={GrainId}; history=[{string.Join(", ", _history)}]; {exception}");
            }
        }

        /// <summary>
        /// Asserts that the activation reached no phase which the test has not read and that the gated callbacks followed the gate protocol.
        /// </summary>
        public void AssertQuiescent()
        {
            Assert.False(Gate.TryReadObservation(out var observation), $"grain={GrainId}; unexpected observation {observation}; history=[{string.Join(", ", _history)}]");
            Assert.Empty(Gate.Violations);
            ActivationAssertionRecorder.AssertNone();
        }

        /// <summary>
        /// Waits until the activation scheduler has run the work which the test queued on it so far.
        /// </summary>
        public Task DrainActivationScheduler() =>
            Activation!.RunOrQueueTask(() => Task.CompletedTask).WaitAsync(Timeout, Cancellation);

        public async ValueTask DisposeAsync()
        {
            Gate.ReleaseAll();
            if (Activation is { } activation && !activation.State.IsDisposingOrDestroyed())
            {
                activation.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Activation state machine test cleanup."), Cancellation);
                await activation.Deactivated.WaitAsync(Timeout, Cancellation);
            }
        }
    }
}

public enum ActivationLifecycleOperation
{
    Activate,
    Release,
    Fail,
    Deactivate,
    Migrate,
    Dispose,
}

internal sealed record ActivationLifecycleRequest(ActivationLifecycleOperation Kind)
{
    public override string ToString() => Kind.ToString();
}

/// <summary>
/// The phase which the activation reached after an operation, its state, whether deactivation has been requested, and whether a
/// migration request was accepted.
/// </summary>
internal sealed record ActivationLifecycleObservation(
    ActivationState? Phase,
    ActivationState? State,
    bool DeactivationRequested,
    bool? MigrationAccepted = null,
    string? Failure = null)
{
    public override string ToString() =>
        Failure ?? $"Phase={Phase}, State={State}, DeactivationRequested={DeactivationRequested}, MigrationAccepted={MigrationAccepted}";
}

[State]
internal partial class ActivationLifecycleModelState : State
{
    /// <summary>
    /// The phase the activation is in, or <see cref="ActivationState.None"/> before it is created.
    /// </summary>
    public ActivationState Phase { get; set; }

    public bool DeactivationRequested { get; set; }
}

/// <summary>
/// The activation state machine, as observed through a grain whose directory registration, lifecycle start, <c>OnActivateAsync</c>,
/// <c>OnDeactivateAsync</c>, lifecycle stop, and directory unregistration each wait for the test to complete or fail them. The grain
/// has no migration context and its silo is the only placement candidate, so rehydration and dehydration complete without waiting.
/// </summary>
internal static class ActivationLifecycleModel
{
    public static bool CanApply(ActivationLifecycleOperation kind, ActivationLifecycleModelState state) => kind switch
    {
        ActivationLifecycleOperation.Activate => state.Phase is ActivationState.None,
        ActivationLifecycleOperation.Release or ActivationLifecycleOperation.Fail => IsGated(state.Phase),
        ActivationLifecycleOperation.Deactivate => state.Phase is not ActivationState.None,
        ActivationLifecycleOperation.Migrate or ActivationLifecycleOperation.Dispose => state.Phase is not (ActivationState.None or ActivationState.Destroyed),
        _ => false,
    };

    /// <summary>
    /// Gets whether a migration request is accepted: from the start of the grain lifecycle until teardown decides whether to migrate.
    /// </summary>
    public static bool AcceptsMigration(ActivationState phase) => phase is >= ActivationState.Starting and < ActivationState.Dehydrating;

    public static void Apply(ActivationLifecycleOperation kind, ActivationLifecycleModelState state)
    {
        var phase = state.Phase;
        var requested = state.DeactivationRequested;
        // A destroyed activation ignores deactivation requests.
        var requests = kind is ActivationLifecycleOperation.Deactivate && phase is not ActivationState.Destroyed
            || kind is ActivationLifecycleOperation.Migrate && AcceptsMigration(phase);
        state.Phase = (kind, phase) switch
        {
            (ActivationLifecycleOperation.Activate, _) => ActivationState.Registering,

            // Forced disposal moves the activation to Disposing and disposes it, skipping every phase which has not run.
            (ActivationLifecycleOperation.Dispose, _) => ActivationState.Destroyed,

            // A request enters teardown only once the activation is active. During setup and teardown, it leaves the state unchanged.
            (ActivationLifecycleOperation.Deactivate or ActivationLifecycleOperation.Migrate, ActivationState.Active) when requests => ActivationState.Deactivating,
            (ActivationLifecycleOperation.Deactivate or ActivationLifecycleOperation.Migrate, _) => phase,

            // Registration which completed, or which threw and so may have committed, is undone by Unregistering. Otherwise, setup continues
            // through Rehydrating, which has no migration context.
            (ActivationLifecycleOperation.Release, ActivationState.Registering) => requested ? ActivationState.Unregistering : ActivationState.Starting,
            (ActivationLifecycleOperation.Fail, ActivationState.Registering) => ActivationState.Unregistering,

            // A deactivation request cancels setup, and a canceled setup phase fails. A failed phase enters teardown at the mirror of the
            // last completed phase, except that lifecycle stop undoes a partial start.
            (ActivationLifecycleOperation.Release, ActivationState.Starting) => requested ? ActivationState.Stopping : ActivationState.Activating,
            (ActivationLifecycleOperation.Release, ActivationState.Activating) => requested ? ActivationState.Stopping : ActivationState.Active,
            (ActivationLifecycleOperation.Fail, ActivationState.Starting or ActivationState.Activating) => ActivationState.Stopping,

            // Teardown continues past failures. Dehydrating does not migrate: the activation is the only placement candidate, and a
            // migration requested during setup is discarded.
            (_, ActivationState.Deactivating) => ActivationState.Stopping,
            (_, ActivationState.Stopping) => ActivationState.Unregistering,
            (_, ActivationState.Unregistering) => ActivationState.Destroyed,
            _ => throw new InvalidOperationException($"{kind} does not apply in {phase}."),
        };

        state.DeactivationRequested = requested
            || requests
            || kind is ActivationLifecycleOperation.Fail && phase.IsSettingUp();
    }

    public static bool IsGated(ActivationState phase) => phase is ActivationState.Registering or ActivationState.Starting
        or ActivationState.Activating or ActivationState.Deactivating or ActivationState.Stopping or ActivationState.Unregistering;
}
internal sealed class ActivationLifecycleSpec : Spec<ActivationLifecycleModelState>
{
    private readonly Dictionary<ActivationLifecycleOperation, ActivationLifecycleModelOperation> _operations = [];

    public ActivationLifecycleSpec()
    {
        foreach (var kind in Enum.GetValues<ActivationLifecycleOperation>())
        {
            var operation = new ActivationLifecycleModelOperation(kind);
            _operations.Add(kind, operation);
            Add(operation);
        }
    }

    public InputSet CreateInputSet()
    {
        var result = new InputSet();
        foreach (var (kind, operation) in _operations)
        {
            result.Add(operation.With(new ActivationLifecycleRequest(kind), kind.ToString()));
        }

        return result;
    }
}

internal sealed class ActivationLifecycleModelOperation(ActivationLifecycleOperation kind)
    : Operation<ActivationLifecycleRequest, ActivationLifecycleObservation, ActivationLifecycleModelState>(kind.ToString())
{
    public override ExpectedOutcomes Apply(ActivationLifecycleRequest request, ActivationLifecycleModelState state)
    {
        var expected = new ActivationLifecycleModelState { Phase = state.Phase, DeactivationRequested = state.DeactivationRequested };
        ActivationLifecycleModel.Apply(request.Kind, expected);
        bool? migrationAccepted = request.Kind is ActivationLifecycleOperation.Migrate ? ActivationLifecycleModel.AcceptsMigration(state.Phase) : null;
        return Expect.That((ActivationLifecycleObservation observation) => Validate(expected, migrationAccepted, observation))
            .ThenState(next => ActivationLifecycleModel.Apply(request.Kind, next));
    }

    public override Task<ActivationLifecycleObservation> ExecuteAsync(TestingContext context, ActivationLifecycleRequest request) =>
        context.Get<ActivationStateMachineTests.ActivationLifecycleExecutor>().Execute(request.Kind);

    private static ValidationResult Validate(ActivationLifecycleModelState expected, bool? migrationAccepted, ActivationLifecycleObservation observation)
    {
        if (observation.Failure is { } failure)
        {
            return ValidationResult.Invalid(failure);
        }

        if (observation.Phase != expected.Phase
            || observation.State != expected.Phase
            || observation.DeactivationRequested != expected.DeactivationRequested
            || observation.MigrationAccepted != migrationAccepted)
        {
            return ValidationResult.Invalid(
                $"Expected Phase={expected.Phase}, State={expected.Phase}, DeactivationRequested={expected.DeactivationRequested}, MigrationAccepted={migrationAccepted}; observed {observation}.");
        }

        return ValidationResult.Valid();
    }
}

/// <summary>
/// A grain directory for <see cref="PhaseGateGrain"/> whose registration and unregistration wait at the grain's <see cref="PhaseGate"/>.
/// </summary>
public sealed class PhaseGateDirectory(PhaseGateRegistry registry) : IGrainDirectory
{
    public Task<GrainAddress?> Register(GrainAddress address) => Register(address, null, CancellationToken.None);

    public async Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress, CancellationToken cancellationToken)
    {
        // The directory call completes only when the test completes or fails it, regardless of cancellation.
        await registry.Get(address.GrainId).Enter(ActivationState.Registering);
        return address;
    }

    public Task Unregister(GrainAddress address) => registry.Get(address.GrainId).Enter(ActivationState.Unregistering);

    public Task<GrainAddress?> Lookup(GrainId grainId) => Task.FromResult<GrainAddress?>(null);

    public Task UnregisterSilos(List<SiloAddress> siloAddresses) => Task.CompletedTask;
}

public sealed class PhaseGateDirectoryResolver(PhaseGateDirectory directory, GrainTypeResolver resolver) : IGrainDirectoryResolver
{
    private readonly GrainType _grainType = resolver.GetGrainType(typeof(PhaseGateGrain));

    public bool TryResolveGrainDirectory(GrainType grainType, GrainProperties? properties, [NotNullWhen(true)] out IGrainDirectory? grainDirectory)
    {
        grainDirectory = grainType == _grainType ? directory : null;
        return grainDirectory is not null;
    }
}

public sealed class PhaseGateRegistry : IDisposable
{
    private readonly ConcurrentDictionary<GrainId, PhaseGate> _gates = new();
    private readonly IDisposable _subscription;

    public PhaseGateRegistry() => _subscription = GrainLifecycleEvents.AllEvents.Subscribe(new ActivatedObserver(this));

    internal PhaseGate Track(GrainId grainId)
    {
        var gate = new PhaseGate();
        Assert.True(_gates.TryAdd(grainId, gate));
        return gate;
    }

    internal PhaseGate Get(GrainId grainId) => _gates[grainId];

    public void ReleaseAll()
    {
        foreach (var gate in _gates.Values)
        {
            gate.ReleaseAll();
        }
    }

    public void Dispose() => _subscription.Dispose();

    private sealed class ActivatedObserver(PhaseGateRegistry registry) : IObserver<GrainLifecycleEvents.LifecycleEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(GrainLifecycleEvents.LifecycleEvent value)
        {
            if (value is GrainLifecycleEvents.Activated && registry._gates.TryGetValue(value.GrainContext.GrainId, out var gate))
            {
                gate.Observe(ActivationState.Active);
            }
        }
    }
}

/// <summary>
/// Holds each gated grain callback until the test completes or fails it, and records the phases which the activation reaches.
/// </summary>
internal sealed class PhaseGate
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly Channel<ActivationState> _observations = Channel.CreateUnbounded<ActivationState>();
#if NET10_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private TaskCompletionSource? _pending;
    private bool _releaseAll;

    /// <summary>
    /// Gets violations of the gate protocol, which are recorded rather than thrown into grain callbacks.
    /// </summary>
    public ConcurrentQueue<string> Violations { get; } = new();

    public void Observe(ActivationState phase)
    {
        if (!_observations.Writer.TryWrite(phase))
        {
            Violations.Enqueue($"Could not record {phase}.");
        }
    }

    public void ObserveDeactivation(ActivationData activation) =>
        activation.Deactivated.ContinueWith(_ => Observe(ActivationState.Destroyed), TaskScheduler.Default);

    public Task Enter(ActivationState phase)
    {
        TaskCompletionSource? pending = null;
        lock (_lock)
        {
            if (!_releaseAll)
            {
                if (_pending is not null)
                {
                    Violations.Enqueue($"{phase} entered while another callback waits.");
                }

                _pending = pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        Observe(phase);
        return pending?.Task ?? Task.CompletedTask;
    }

    public void Release() => TakePending().SetResult();

    public void Fail() => TakePending().SetException(new InvalidOperationException("Injected phase failure."));

    public void ReleaseAll()
    {
        TaskCompletionSource? pending;
        lock (_lock)
        {
            _releaseAll = true;
            pending = _pending;
            _pending = null;
        }

        pending?.TrySetResult();
    }

    public async Task<ActivationState> ReadObservation() =>
        await _observations.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);

    public bool TryReadObservation(out ActivationState phase) => _observations.Reader.TryRead(out phase);

    private TaskCompletionSource TakePending()
    {
        lock (_lock)
        {
            var pending = _pending ?? throw new InvalidOperationException("No grain callback is waiting.");
            _pending = null;
            return pending;
        }
    }
}

public interface IPhaseGateGrain : IGrainWithStringKey
{
    Task Ping();
}

/// <summary>
/// A grain whose lifecycle start, <see cref="OnActivateAsync"/>, <see cref="OnDeactivateAsync"/>, and lifecycle stop each wait at a <see cref="PhaseGate"/>,
/// as do its registration and unregistration in <see cref="PhaseGateDirectory"/>.
/// The callbacks ignore cancellation, modeling user code which runs to completion after its token is canceled.
/// </summary>
public sealed class PhaseGateGrain(PhaseGateRegistry registry) : Grain, IPhaseGateGrain, ILifecycleParticipant<IGrainLifecycle>
{
    private PhaseGate Gate => registry.Get(this.GetGrainId());

    public Task Ping() => Task.CompletedTask;

    public void Participate(IGrainLifecycle lifecycle) =>
        lifecycle.Subscribe<PhaseGateGrain>(
            GrainLifecycleStage.SetupState,
            _ => Gate.Enter(ActivationState.Starting),
            _ => Gate.Enter(ActivationState.Stopping));

    public override Task OnActivateAsync(CancellationToken cancellationToken) => Gate.Enter(ActivationState.Activating);

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken) => Gate.Enter(ActivationState.Deactivating);
}

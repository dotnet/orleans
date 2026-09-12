#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Orleans.GrainDirectory;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.MembershipService;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT"), TestCategory("Migration")]
public class ActivationDataMigrationTests(ActivationDataMigrationTests.Fixture fixture) : IClassFixture<ActivationDataMigrationTests.Fixture>
{
    private readonly Fixture _fixture = fixture;

    private InProcessSiloHandle PrimarySilo => (InProcessSiloHandle)_fixture.HostedCluster.Primary!;

    [Fact]
    public async Task TryStartMigration_ReturnsTrue_WhenActivationCanStartMigration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var activation = await GetActivation(cancellationToken);

        Assert.True(activation.TryStartMigration(requestContext: null, cancellationToken));

        Assert.Equal(ActivationState.Deactivating, activation.State);

        await activation.Deactivated.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    [Fact]
    public async Task TryStartMigration_ReturnsFalse_WhenActivationIsInvalid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var activation = await GetActivation(cancellationToken);
        var originalDeactivated = activation.Deactivated;
        activation.Deactivate(new DeactivationReason(DeactivationReasonCode.RuntimeRequested, "test"), cancellationToken);
        await originalDeactivated.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        Assert.Equal(ActivationState.Invalid, activation.State);
        Assert.False(activation.TryStartMigration(requestContext: null, cancellationToken));
    }

    [Fact]
    public async Task TryDeactivateForCollection_AtomicallyStartsDeactivation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var activation = await GetActivation(cancellationToken);
        var deactivated = activation.Deactivated;
        var reason = new DeactivationReason(DeactivationReasonCode.ActivationIdle, "test");

        var result = ((ICollectibleGrainContext)activation).TryDeactivateForCollection(
            reason,
            DateTime.UtcNow,
            TimeSpan.Zero,
            respectKeepAlive: true,
            cancellationToken);

        Assert.Equal(ActivationCollectionAction.StartedDeactivation, result.Action);
        await deactivated.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        Assert.Equal(ActivationState.Invalid, activation.State);
    }

    [Fact]
    public async Task TryStartMigration_DoesNotAcquireActivationInstanceMonitor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var activation = await GetActivation(cancellationToken);

        var lockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseLock = new ManualResetEventSlim();
        var lockHolder = Task.Factory.StartNew(
            () =>
            {
                lock (activation)
                {
                    lockAcquired.SetResult();
                    if (!releaseLock.Wait(TimeSpan.FromSeconds(10), cancellationToken))
                    {
                        throw new TimeoutException("Timed out waiting to release the activation instance monitor.");
                    }
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        await lockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        try
        {
            Assert.True(
                await Task.Run(
                    () => activation.TryStartMigration(requestContext: null, cancellationToken),
                    cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
        }
        finally
        {
            releaseLock.Set();
            await lockHolder.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

        await activation.Deactivated.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    private async Task<ActivationData> GetActivation(CancellationToken cancellationToken)
    {
        var grain = _fixture.GrainFactory.GetGrain<IIdleActivationGcTestGrain1>(Guid.NewGuid());
        await grain.Nop().WaitAsync(cancellationToken);

        var grainId = ((GrainReference)grain).GrainId;
        var directory = PrimarySilo.SiloHost.Services.GetRequiredService<ActivationDirectory>();
        return Assert.IsType<ActivationData>(directory.FindTarget(grainId));
    }

    public class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
        }
    }
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT"), TestCategory("Migration")]
public class ActivationDataMigrationTestsRuntimeMetrics
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("ordinary")]
    [InlineData("named")]
    [InlineData("int")]
    [InlineData("string")]
    public async Task CanonicalRuntimeTypes_ReuseSharedMetricsAcrossActualActivations(string kind)
    {
        await using var fixture = new MetricsFixture();
        await fixture.InitializeAsync();
        var services = fixture.PrimaryServices;
        using var metrics = new RuntimeMetrics(services);
        using var events = new DiagnosticEventCollector(GrainLifecycleEvents.ListenerName);
        var baseline = metrics.Snapshot();
        var firstCall = GetCall(fixture.GrainFactory, kind, 101);
        var secondCall = GetCall(fixture.GrainFactory, kind, 202);
        var type = firstCall.Reference.GrainId.Type;

        var first = await InvokeAndObserve(services, metrics, events, firstCall, 1);
        var second = await InvokeAndObserve(services, metrics, events, secondCall, 2);
        var shared = AssertCanonicalContext(services, first);
        Assert.NotSame(first, second);
        Assert.Same(shared, second.Shared);
        Assert.Same(first.Shared.GrainTypeMetrics, second.Shared.GrainTypeMetrics);
        Assert.Same(first.Shared.GrainTypeMetricName, second.Shared.GrainTypeMetricName);
        if (kind == "named")
        {
            Assert.Equal("guid-test-grain", shared.GrainTypeMetricName);
        }

        AssertCounts(services, metrics, baseline, shared, 2, 2, 2);
        AssertActivationEvents(metrics, shared, created: 2, destroyed: 0, failed: 0, instances: [1, 1]);
        AssertActivationLatencies(metrics, shared, "success", "success");

        // Attach a second listener only after both activations exist. Gauges must be snapshots,
        // not a reconstruction from creation events observed by this listener.
        using var lateListener = new RuntimeMetrics(services);
        Assert.Empty(lateListener.For(InstrumentNames.CATALOG_ACTIVATION_CREATED, type.ToString()));
        AssertCounts(services, lateListener, baseline, shared, 2, 2, 2);
        AssertCounts(services, lateListener, baseline, shared, 2, 2, 2);

        var catalog = services.GetRequiredService<Catalog>();
        Assert.Same(first, catalog.GetOrCreateActivation(first.GrainId, null, null));
        await firstCall.Invoke().WaitAsync(Timeout, TestCancellation);
        Assert.Same(first, services.GetRequiredService<ActivationDirectory>().FindTarget(first.GrainId));
        AssertActivationEvents(metrics, shared, created: 2, destroyed: 0, failed: 0, instances: [1, 1]);

        await Deactivate(first);
        AssertCounts(services, lateListener, baseline, shared, 1, 1, 1);
        var replacement = await InvokeAndObserve(services, metrics, events, firstCall, 3, first);
        Assert.NotSame(first, replacement);
        Assert.NotEqual(first.ActivationId, replacement.ActivationId);
        Assert.Same(shared, replacement.Shared);
        Assert.Same(shared.GrainTypeMetricName, replacement.Shared.GrainTypeMetricName);

        // An obsolete context must not remove the replacement which owns the same GrainId.
        catalog.UnregisterMessageTarget(first);
        catalog.UnregisterMessageTarget(first);
        Assert.Same(replacement, services.GetRequiredService<ActivationDirectory>().FindTarget(first.GrainId));
        AssertActivationEvents(metrics, shared, created: 3, destroyed: 1, failed: 0, instances: [1, 1, -1, 1]);
        AssertCounts(services, lateListener, baseline, shared, 2, 2, 2);

        await Deactivate(replacement);
        await Deactivate(second);
        catalog.UnregisterMessageTarget(replacement);
        catalog.UnregisterMessageTarget(second);
        AssertCounts(services, lateListener, baseline, shared, 0, 0, 0);
        AssertCounts(services, lateListener, baseline, shared, 0, 0, 0);
        AssertActivationEvents(metrics, shared, created: 3, destroyed: 3, failed: 0, instances: [1, 1, -1, 1, -1, -1]);
        AssertActivationLatencies(metrics, shared, "success", "success", "success");
        AssertDeactivationMetrics(metrics, shared, "deactivateOnIdle", "deactivateOnIdle", "deactivateOnIdle");
        Assert.All(metrics.ForType(shared.GrainTypeMetricName), sample => Assert.Same(shared.GrainTypeMetricName, sample.Tags["grain_type"]));
        Assert.All(lateListener.ForType(shared.GrainTypeMetricName), sample => Assert.Same(shared.GrainTypeMetricName, sample.Tags["grain_type"]));
    }

    [Fact]
    public async Task ClosedGenericRuntimeTypes_HaveDistinctBuckets()
    {
        await using var fixture = new MetricsFixture();
        await fixture.InitializeAsync();
        var services = fixture.PrimaryServices;
        using var metrics = new RuntimeMetrics(services);
        using var events = new DiagnosticEventCollector(GrainLifecycleEvents.ListenerName);
        var baseline = metrics.Snapshot();
        var integer = await InvokeAndObserve(services, metrics, events, GetCall(fixture.GrainFactory, "int", 301), 1);
        var text = await InvokeAndObserve(services, metrics, events, GetCall(fixture.GrainFactory, "string", 301), 1);
        var integerShared = AssertCanonicalContext(services, integer);
        var textShared = AssertCanonicalContext(services, text);

        Assert.NotEqual(integer.GrainId.Type, text.GrainId.Type);
        Assert.NotSame(integerShared.GrainTypeMetrics, textShared.GrainTypeMetrics);
        Assert.NotSame(integerShared.GrainTypeMetricName, textShared.GrainTypeMetricName);
        AssertCounts(services, metrics, baseline, integerShared, 1, 1, 1);
        AssertCounts(services, metrics, baseline, textShared, 1, 1, 1);

        await Deactivate(integer);
        AssertCounts(services, metrics, baseline, integerShared, 0, 0, 0);
        AssertCounts(services, metrics, baseline, textShared, 1, 1, 1);
        Assert.Same(text, services.GetRequiredService<ActivationDirectory>().FindTarget(text.GrainId));
        AssertActivationEvents(metrics, textShared, created: 1, destroyed: 0, failed: 0, instances: [1]);
        Assert.Empty(metrics.For(InstrumentNames.CATALOG_ACTIVATION_SHUTDOWN, textShared.GrainTypeMetricName));
        await Deactivate(text);
        AssertCounts(services, metrics, baseline, textShared, 0, 0, 0);
        AssertActivationEvents(metrics, integerShared, created: 1, destroyed: 1, failed: 0, instances: [1, -1]);
        AssertActivationEvents(metrics, textShared, created: 1, destroyed: 1, failed: 0, instances: [1, -1]);
    }

    [Fact]
    public async Task Runtime_ActivationFailurePreservesFailureAndLatencyAccounting()
    {
        await using var fixture = new MetricsFixture("activation-error");
        await fixture.InitializeAsync();
        var services = fixture.PrimaryServices;
        var control = services.GetRequiredService<LifecycleControl>();
        using var metrics = new RuntimeMetrics(services);
        using var events = new DiagnosticEventCollector(GrainLifecycleEvents.ListenerName);
        var baseline = metrics.Snapshot();
        var id = GetCall(fixture.GrainFactory, "ordinary", 401).Reference.GrainId;
        var recorded = metrics.WaitForCount(InstrumentNames.CATALOG_ACTIVATION_LATENCY, id.Type.ToString(), 1);
        var deactivating = events.WaitForEventAsync(nameof(GrainLifecycleEvents.Deactivating),
            e => e.Payload is GrainLifecycleEvents.Deactivating d && d.GrainContext.GrainId == id, Timeout, TestCancellation);
        var activation = Assert.IsType<ActivationData>(services.GetRequiredService<Catalog>().GetOrCreateActivation(id, null, null));
        await control.ActivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Same(activation, control.Captured);
        Assert.Equal(ActivationState.Activating, activation.State);
        AssertCounts(services, metrics, baseline, activation.Shared, 1, 0, 1);
        Assert.Empty(metrics.For(InstrumentNames.CATALOG_ACTIVATION_LATENCY, id.Type.ToString()));
        control.ActivationRelease.TrySetResult();

        var reason = Assert.IsType<GrainLifecycleEvents.Deactivating>((await deactivating).Payload).Reason;
        await recorded;
        await activation.Deactivated.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(DeactivationReasonCode.ActivationFailed, reason.ReasonCode);
        Assert.Same(control.Failure, reason.Exception);
        Assert.Equal(ActivationState.Invalid, activation.State);
        AssertActivationLatencies(metrics, activation.Shared, "error");
        AssertActivationEvents(metrics, activation.Shared, created: 1, destroyed: 1, failed: 1, instances: [1, -1]);
        AssertDeactivationMetrics(metrics, activation.Shared, "deactivateOnIdle");
        AssertCounts(services, metrics, baseline, activation.Shared, 0, 0, 0);
        Assert.Equal(1, control.ObserverDestroyed);
        Assert.Equal(1, control.ScopeDisposed);
        Assert.DoesNotContain(events.Events, e => e.Payload is GrainLifecycleEvents.Activated a && a.GrainContext.GrainId == id);
    }

    [Fact]
    public async Task Runtime_ActivationCancellationUsesTheActualToken()
    {
        await using var fixture = new MetricsFixture("activation-canceled");
        await fixture.InitializeAsync();
        var services = fixture.PrimaryServices;
        var control = services.GetRequiredService<LifecycleControl>();
        using var metrics = new RuntimeMetrics(services);
        using var events = new DiagnosticEventCollector(GrainLifecycleEvents.ListenerName);
        using var cancellation = new CancellationTokenSource();
        var baseline = metrics.Snapshot();
        var id = GetCall(fixture.GrainFactory, "ordinary", 402).Reference.GrainId;
        var recorded = metrics.WaitForCount(InstrumentNames.CATALOG_ACTIVATION_LATENCY, id.Type.ToString(), 1);
        var activation = Assert.IsType<ActivationData>(services.GetRequiredService<GrainContextActivator>().CreateInstance(
            new GrainAddress { GrainId = id, ActivationId = ActivationId.NewId(), SiloAddress = fixture.HostedCluster.Primary!.SiloAddress }));
        try
        {
            activation.Activate(null, cancellation.Token);
            await control.ActivationEntered.Task.WaitAsync(Timeout, TestCancellation);
            Assert.True(control.ActivationToken.CanBeCanceled);
            Assert.False(control.ActivationToken.IsCancellationRequested);
            // This context is real, but intentionally not registered with Catalog.
            AssertCounts(services, metrics, baseline, activation.Shared, 0, 0, 1);
            cancellation.Cancel();
            await control.ActivationExited.Task.WaitAsync(Timeout, TestCancellation);
            await recorded;
            await activation.Deactivated.WaitAsync(Timeout, TestCancellation);

            Assert.True(control.ActivationToken.IsCancellationRequested);
            Assert.Equal(ActivationState.Invalid, activation.State);
            AssertActivationLatencies(metrics, activation.Shared, "canceled");
            AssertActivationEvents(metrics, activation.Shared, created: 0, destroyed: 0, failed: 1, instances: [1, -1]);
            AssertDeactivationMetrics(metrics, activation.Shared, "deactivateOnIdle");
            AssertCounts(services, metrics, baseline, activation.Shared, 0, 0, 0);
            Assert.Equal(1, control.ObserverDestroyed);
            Assert.Equal(1, control.ScopeDisposed);
            Assert.DoesNotContain(events.Events, e => e.Payload is GrainLifecycleEvents.Activated a && a.GrainContext.GrainId == id);
        }
        finally
        {
            cancellation.Cancel();
            control.ActivationRelease.TrySetResult();
            await activation.DisposeAsync();
        }
    }

    [Fact]
    public async Task Runtime_ConstructorFailureDoesNotDecrementANonexistentInstance()
    {
        await using var fixture = new MetricsFixture("constructor-error");
        await fixture.InitializeAsync();
        var services = fixture.PrimaryServices;
        var control = services.GetRequiredService<LifecycleControl>();
        using var metrics = new RuntimeMetrics(services);
        using var events = new DiagnosticEventCollector(GrainLifecycleEvents.ListenerName);
        var baseline = metrics.Snapshot();
        var id = GetCall(fixture.GrainFactory, "ordinary", 403).Reference.GrainId;
        var activation = Assert.IsType<ActivationData>(services.GetRequiredService<Catalog>().GetOrCreateActivation(id, null, null));
        await activation.Deactivated.WaitAsync(Timeout, TestCancellation);
        await Task.WhenAll(activation.DisposeAsync().AsTask(), activation.DisposeAsync().AsTask());

        Assert.Same(activation, control.Captured);
        Assert.Null(activation.GrainInstance);
        Assert.Equal(ActivationState.Invalid, activation.State);
        Assert.Equal(0, control.ObserverCreated);
        Assert.Equal(1, control.ObserverDestroyed);
        Assert.Equal(0, control.DisposeInvocations);
        Assert.Equal(1, control.ScopeDisposed);
        AssertActivationEvents(metrics, activation.Shared, created: 1, destroyed: 1, failed: 0, instances: []);
        Assert.Empty(metrics.For(InstrumentNames.CATALOG_ACTIVATION_LATENCY, id.Type.ToString()));
        AssertDeactivationMetrics(metrics, activation.Shared, "deactivateOnIdle");
        AssertCounts(services, metrics, baseline, activation.Shared, 0, 0, 0);
        Assert.DoesNotContain(events.Events, e => e.Payload is GrainLifecycleEvents.Created c && c.GrainContext.GrainId == id);
        var reason = Assert.IsType<GrainLifecycleEvents.Deactivating>(Assert.Single(events.GetEvents(nameof(GrainLifecycleEvents.Deactivating)),
            e => e.Payload is GrainLifecycleEvents.Deactivating d && d.GrainContext.GrainId == id).Payload).Reason;
        Assert.Equal(DeactivationReasonCode.ActivationFailed, reason.ReasonCode);
        Assert.Same(control.Failure, reason.Exception);
    }

    [Theory]
    [InlineData("dispose-normal", false)]
    [InlineData("dispose-object-disposed", false)]
    [InlineData("dispose-error", false)]
    [InlineData("dispose-normal", true)]
    [InlineData("dispose-object-disposed", true)]
    [InlineData("dispose-error", true)]
    public async Task Runtime_DisposalAccountsOnceEvenWhenTheDisposerThrows(string mode, bool lifecycle)
    {
        await using var fixture = new MetricsFixture(mode);
        await fixture.InitializeAsync();
        var services = fixture.PrimaryServices;
        var control = services.GetRequiredService<LifecycleControl>();
        using var metrics = new RuntimeMetrics(services);
        using var events = new DiagnosticEventCollector(GrainLifecycleEvents.ListenerName);
        var baseline = metrics.Snapshot();
        var activation = await InvokeAndObserve(services, metrics, events, GetCall(fixture.GrainFactory, "ordinary", 404), 1);
        AssertCounts(services, metrics, baseline, activation.Shared, 1, 1, 1);
        Task firstDisposal;
        if (lifecycle)
        {
            firstDisposal = activation.Deactivated;
            activation.Deactivate(new(DeactivationReasonCode.RuntimeRequested, "metrics disposal test"), TestCancellation);
        }
        else
        {
            firstDisposal = activation.DisposeAsync().AsTask();
        }

        await control.DisposalEntered.Task.WaitAsync(Timeout, TestCancellation);
        var secondDisposal = activation.DisposeAsync().AsTask();
        try
        {
            // The first caller is suspended inside user disposal. A second joined caller must
            // neither invoke that disposer again nor decrement object accounting early.
            await secondDisposal.WaitAsync(Timeout, TestCancellation);
            Assert.Equal(1, control.DisposeInvocations);
            Assert.Equal(0, control.ScopeDisposed);
            Assert.Equal([1d], metrics.For(InstrumentNames.GRAIN_COUNTS, activation.Shared.GrainTypeMetricName).Select(s => s.Value));
        }
        finally
        {
            control.DisposalRelease.TrySetResult();
            try
            {
                await Task.WhenAll(firstDisposal, secondDisposal).WaitAsync(Timeout, TestCancellation);
            }
            catch (InvalidOperationException) when (!lifecycle && mode == "dispose-error")
            {
                // The expected user-disposal exception is asserted below, after both callers
                // have completed. Always release and join them, even if the once-only guard fails.
            }
        }

        if (!lifecycle && mode == "dispose-error")
        {
            Assert.Same(control.Failure, await Assert.ThrowsAsync<InvalidOperationException>(() => firstDisposal));
        }
        else
        {
            await firstDisposal.WaitAsync(Timeout, TestCancellation);
        }

        await activation.DisposeAsync();
        Assert.Equal(1, control.DisposeInvocations);
        Assert.Equal(1, control.ObserverCreated);
        Assert.Equal(1, control.ObserverDestroyed);
        Assert.Equal(1, control.ScopeDisposed);
        Assert.Equal(ActivationState.Invalid, activation.State);
        if (lifecycle)
        {
            AssertDeactivationMetrics(metrics, activation.Shared, "deactivateOnIdle");
        }
        else
        {
            // Direct disposal is not catalog removal or FinishDeactivating.
            AssertCounts(services, metrics, baseline, activation.Shared, 1, 0, 0);
            Assert.Empty(metrics.For(InstrumentNames.CATALOG_ACTIVATION_SHUTDOWN, activation.Shared.GrainTypeMetricName));
            Assert.Empty(metrics.For(InstrumentNames.CATALOG_DEACTIVATION_LATENCY, activation.Shared.GrainTypeMetricName));
            services.GetRequiredService<Catalog>().UnregisterMessageTarget(activation);
        }

        services.GetRequiredService<Catalog>().UnregisterMessageTarget(activation);
        AssertActivationEvents(metrics, activation.Shared, created: 1, destroyed: 1, failed: 0, instances: [1, -1]);
        AssertCounts(services, metrics, baseline, activation.Shared, 0, 0, 0);
    }

    [Fact]
    public async Task Runtime_SuccessfulMigrationTransfersToAnotherSilo()
    {
        await using var fixture = new MetricsFixture(siloCount: 2);
        await fixture.InitializeAsync();
        var silos = fixture.HostedCluster.Silos.Cast<InProcessSiloHandle>().ToArray();
        using var firstMetrics = new RuntimeMetrics(silos[0].SiloHost.Services);
        using var secondMetrics = new RuntimeMetrics(silos[1].SiloHost.Services);
        using var events = new DiagnosticEventCollector(GrainLifecycleEvents.ListenerName);
        var baselines = new[] { firstMetrics.Snapshot(), secondMetrics.Snapshot() };
        var observers = new[] { firstMetrics, secondMetrics };
        var call = GetCall(fixture.GrainFactory, "named", 501);
        var initialActivation = events.WaitForEventAsync(nameof(GrainLifecycleEvents.Activated),
            e => e.Payload is GrainLifecycleEvents.Activated a && a.GrainContext.GrainId == call.Reference.GrainId, Timeout, TestCancellation);
        await call.Invoke().WaitAsync(Timeout, TestCancellation);
        var source = Assert.IsType<ActivationData>(Assert.IsType<GrainLifecycleEvents.Activated>((await initialActivation).Payload).GrainContext);
        var sourceIndex = Array.FindIndex(silos, silo => silo.SiloAddress == source.Address.SiloAddress);
        var destinationIndex = 1 - sourceIndex;
        var sourceServices = silos[sourceIndex].SiloHost.Services;
        var destinationServices = silos[destinationIndex].SiloHost.Services;
        var sourceMetrics = observers[sourceIndex];
        var destinationMetrics = observers[destinationIndex];
        await sourceMetrics.WaitForCount(InstrumentNames.CATALOG_ACTIVATION_LATENCY, source.Shared.GrainTypeMetricName, 1);
        AssertCanonicalContext(sourceServices, source);
        AssertCounts(sourceServices, sourceMetrics, baselines[sourceIndex], source.Shared, 1, 1, 1);
        var destinationReady = events.WaitForEventAsync(nameof(GrainLifecycleEvents.Activated),
            e => e.Payload is GrainLifecycleEvents.Activated a && a.GrainContext.GrainId == source.GrainId
                && a.GrainContext.Address.SiloAddress == silos[destinationIndex].SiloAddress, Timeout, TestCancellation);
        var destinationRecorded = destinationMetrics.WaitForCount(InstrumentNames.CATALOG_ACTIVATION_LATENCY, source.Shared.GrainTypeMetricName, 1);
        var sourceComplete = source.Deactivated;
        source.ForwardingAddress = silos[destinationIndex].SiloAddress;
        Assert.True(source.TryStartMigration(null, TestCancellation));
        await sourceComplete.WaitAsync(Timeout, TestCancellation);
        var destination = Assert.IsType<ActivationData>(Assert.IsType<GrainLifecycleEvents.Activated>((await destinationReady).Payload).GrainContext);
        await destinationRecorded;

        Assert.Equal(ActivationState.Invalid, source.State);
        Assert.Equal(ActivationState.Valid, destination.State);
        Assert.NotEqual(source.ActivationId, destination.ActivationId);
        Assert.Equal(source.GrainId, destination.GrainId);
        Assert.Equal(silos[destinationIndex].SiloAddress, await ((IGuidTestGrain)call.Reference).GetSiloAddress());
        Assert.Same(destination, destinationServices.GetRequiredService<ActivationDirectory>().FindTarget(source.GrainId));
        Assert.Null(sourceServices.GetRequiredService<ActivationDirectory>().FindTarget(source.GrainId));
        AssertCanonicalContext(destinationServices, destination);
        Assert.Equal(source.Shared.GrainTypeMetricName, destination.Shared.GrainTypeMetricName);
        Assert.NotSame(source.Shared.GrainTypeMetrics, destination.Shared.GrainTypeMetrics);
        AssertCounts(sourceServices, sourceMetrics, baselines[sourceIndex], source.Shared, 0, 0, 0);
        AssertCounts(destinationServices, destinationMetrics, baselines[destinationIndex], destination.Shared, 1, 1, 1);
        AssertActivationEvents(sourceMetrics, source.Shared, created: 1, destroyed: 1, failed: 0, instances: [1, -1]);
        AssertActivationEvents(destinationMetrics, destination.Shared, created: 1, destroyed: 0, failed: 0, instances: [1]);
        AssertActivationLatencies(sourceMetrics, source.Shared, "success");
        AssertActivationLatencies(destinationMetrics, destination.Shared, "success");
        AssertDeactivationMetrics(sourceMetrics, source.Shared, "migration");
        Assert.Empty(destinationMetrics.For(InstrumentNames.CATALOG_ACTIVATION_SHUTDOWN, destination.Shared.GrainTypeMetricName));
        await Deactivate(destination);
        AssertCounts(destinationServices, destinationMetrics, baselines[destinationIndex], destination.Shared, 0, 0, 0);
        AssertActivationEvents(destinationMetrics, destination.Shared, created: 1, destroyed: 1, failed: 0, instances: [1, -1]);
        AssertDeactivationMetrics(destinationMetrics, destination.Shared, "deactivateOnIdle");
    }

    [Fact]
    public async Task Runtime_SelfPlacementDoesNotRecordSuccessfulMigration()
    {
        await using var fixture = new MetricsFixture();
        await fixture.InitializeAsync();
        var services = fixture.PrimaryServices;
        using var metrics = new RuntimeMetrics(services);
        using var events = new DiagnosticEventCollector(GrainLifecycleEvents.ListenerName);
        var baseline = metrics.Snapshot();
        var call = GetCall(fixture.GrainFactory, "ordinary", 502);
        var activation = await InvokeAndObserve(services, metrics, events, call, 1);
        var completed = activation.Deactivated;
        Assert.True(activation.TryStartMigration(null, TestCancellation));
        await completed.WaitAsync(Timeout, TestCancellation);

        Assert.Equal(ActivationState.Invalid, activation.State);
        Assert.False(activation.TryStartMigration(null, TestCancellation));
        Assert.Null(activation.ForwardingAddress);
        AssertDeactivationMetrics(metrics, activation.Shared, "deactivateOnIdle");
        AssertActivationEvents(metrics, activation.Shared, created: 1, destroyed: 1, failed: 0, instances: [1, -1]);
        AssertCounts(services, metrics, baseline, activation.Shared, 0, 0, 0);
        var replacement = await InvokeAndObserve(services, metrics, events, call, 2, activation);
        Assert.NotSame(activation, replacement);
        Assert.Same(activation.Shared, replacement.Shared);
        await Deactivate(replacement);
        AssertDeactivationMetrics(metrics, activation.Shared, "deactivateOnIdle", "deactivateOnIdle");
        AssertCounts(services, metrics, baseline, activation.Shared, 0, 0, 0);
    }

    [Theory]
    [InlineData("directory-error", false, "directory_error")]
    [InlineData("directory-error", true, "canceled")]
    [InlineData("directory-duplicate", false, "duplicate")]
    [InlineData("directory-duplicate", true, "duplicate")]
    public async Task Runtime_DirectoryOutcomesKeepDistinctMetrics(string mode, bool cancel, string status)
    {
        await using var fixture = new MetricsFixture(mode, siloCount: mode == "directory-duplicate" ? (short)2 : (short)1);
        await fixture.InitializeAsync();
        var services = fixture.PrimaryServices;
        var control = services.GetRequiredService<LifecycleControl>();
        using var metrics = new RuntimeMetrics(services);
        using var events = new DiagnosticEventCollector(GrainLifecycleEvents.ListenerName);
        using var cancellation = new CancellationTokenSource();
        var baseline = metrics.Snapshot();
        var id = GetCall(fixture.GrainFactory, "ordinary", 601).Reference.GrainId;
        var recorded = metrics.WaitForCount(InstrumentNames.CATALOG_ACTIVATION_LATENCY, id.Type.ToString(), 1);
        var activation = Assert.IsType<ActivationData>(services.GetRequiredService<GrainContextActivator>().CreateInstance(
            new GrainAddress { GrainId = id, ActivationId = ActivationId.NewId(), SiloAddress = fixture.HostedCluster.Primary!.SiloAddress }));
        try
        {
            activation.Activate(null, cancellation.Token);
            var registration = await control.DirectoryEntered.Task.WaitAsync(Timeout, TestCancellation);
            Assert.True(activation.Address.Matches(registration));
            Assert.Same(services.GetRequiredService<ControlledDirectory>(), activation.Shared.GrainDirectory);
            Assert.False(control.DirectoryToken.IsCancellationRequested);
            AssertCounts(services, metrics, baseline, activation.Shared, 0, 0, 1);
            Assert.Empty(metrics.For(InstrumentNames.CATALOG_ACTIVATION_LATENCY, id.Type.ToString()));
            if (cancel) cancellation.Cancel();
            Assert.Equal(cancel, control.DirectoryToken.IsCancellationRequested);

            GrainAddress? existing = null;
            if (mode == "directory-error")
            {
                control.DirectoryResponse.TrySetException(control.Failure);
            }
            else
            {
                existing = new GrainAddress
                {
                    GrainId = id,
                    ActivationId = ActivationId.NewId(),
                    SiloAddress = Assert.Single(fixture.HostedCluster.SecondarySilos).SiloAddress,
                    MembershipVersion = registration.MembershipVersion,
                };
                // Deliberately return a successful competing registration even when canceled:
                // a no-exception duplicate result takes precedence over cancellation.
                control.DirectoryResponse.TrySetResult(existing);
            }

            await recorded;
            await activation.Deactivated.WaitAsync(Timeout, TestCancellation);
            Assert.Equal(1, control.DirectoryRegistrations);
            Assert.Equal(ActivationState.Invalid, activation.State);
            Assert.Equal(existing?.SiloAddress, activation.ForwardingAddress);
            AssertActivationLatencies(metrics, activation.Shared, status);
            AssertActivationEvents(metrics, activation.Shared, created: 0, destroyed: 0, failed: 0,
                instances: [1, -1], concurrent: existing is null ? 0 : 1);
            AssertDeactivationMetrics(metrics, activation.Shared, "deactivateOnIdle");
            AssertCounts(services, metrics, baseline, activation.Shared, 0, 0, 0);
            Assert.Equal(1, control.ObserverDestroyed);
            Assert.Equal(1, control.ScopeDisposed);
            Assert.DoesNotContain(events.Events, e => e.Payload is GrainLifecycleEvents.Activated a && a.GrainContext.GrainId == id);
        }
        finally
        {
            control.DirectoryResponse.TrySetResult(null);
            cancellation.Cancel();
            await activation.DisposeAsync();
        }
    }

    [Fact]
    public async Task Runtime_StatelessWorkerCountsParentAndWorkerAtTheirOwnScopes()
    {
        await using var fixture = new MetricsFixture();
        await fixture.InitializeAsync();
        var services = fixture.PrimaryServices;
        using var metrics = new RuntimeMetrics(services);
        using var events = new DiagnosticEventCollector(GrainLifecycleEvents.ListenerName);
        var baseline = metrics.Snapshot();
        var grain = fixture.GrainFactory.GetGrain<IStatelessWorkerActivationCollectorTestGrain1>(Guid.NewGuid());
        var id = ((GrainReference)grain).GrainId;
        var workerReady = events.WaitForEventAsync(nameof(GrainLifecycleEvents.Activated),
            e => e.Payload is GrainLifecycleEvents.Activated a && a.GrainContext.GrainId == id, Timeout, TestCancellation);
        var activated = metrics.WaitForCount(InstrumentNames.CATALOG_ACTIVATION_LATENCY, id.Type.ToString(), 1);
        await grain.Nop().WaitAsync(Timeout, TestCancellation);
        var worker = Assert.IsType<ActivationData>(Assert.IsType<GrainLifecycleEvents.Activated>((await workerReady).Payload).GrainContext);
        await activated;
        var parent = Assert.IsType<StatelessWorkerGrainContext>(services.GetRequiredService<ActivationDirectory>().FindTarget(id));
        Assert.NotSame(parent, worker);
        Assert.NotEqual(parent.ActivationId, worker.ActivationId);
        Assert.Null(parent.GrainInstance);
        Assert.IsType<UnitTests.Grains.StatelessWorkerActivationCollectorTestGrain1>(worker.GrainInstance);
        var shared = AssertCanonicalContext(services, worker);
        AssertCounts(services, metrics, baseline, shared, 1, 1, 1);
        AssertActivationEvents(metrics, shared, created: 1, destroyed: 0, failed: 0, instances: [1]);
        var latency = Assert.Single(metrics.For(InstrumentNames.CATALOG_ACTIVATION_LATENCY, shared.GrainTypeMetricName));
        AssertTypeTag(latency, shared, "directory", "grain_type", "status");
        Assert.Equal("disabled", latency.Tags["directory"]);
        Assert.Equal("success", latency.Tags["status"]);
        Assert.Equal("ms", latency.Instrument.Unit);
        Assert.True(double.IsFinite(latency.Value) && latency.Value >= 0);

        // Worker completion and parent catalog removal are different asynchronous
        // boundaries: OnDestroyActivation enqueues removal on the parent's loop.
        var removed = metrics.WaitForCount(InstrumentNames.CATALOG_ACTIVATION_DESTROYED, shared.GrainTypeMetricName, 1);
        var parentCompleted = parent.Deactivated;
        parent.Deactivate(new(DeactivationReasonCode.RuntimeRequested, "stateless metrics cleanup"), TestCancellation);
        await parentCompleted.WaitAsync(Timeout, TestCancellation);
        await worker.Deactivated.WaitAsync(Timeout, TestCancellation);
        await removed;
        Assert.Null(services.GetRequiredService<ActivationDirectory>().FindTarget(id));
        AssertCounts(services, metrics, baseline, shared, 0, 0, 0);
        AssertActivationEvents(metrics, shared, created: 1, destroyed: 1, failed: 0, instances: [1, -1]);
        AssertDeactivationMetrics(metrics, shared, "deactivateOnIdle");
        await parent.DisposeAsync();
    }

    [Fact]
    public async Task Runtime_StatelessWorkerFanoutCountsOneParentAndTwoInstances()
    {
        await using var fixture = new MetricsFixture("stateless-fanout");
        await fixture.InitializeAsync();
        var services = fixture.PrimaryServices;
        var gate = services.GetRequiredService<WorkerCallGate>();
        using var metrics = new RuntimeMetrics(services);
        var baseline = metrics.Snapshot();
        var grain = fixture.GrainFactory.GetGrain<IStatelessWorkerActivationCollectorTestGrain1>(Guid.NewGuid());
        var id = ((GrainReference)grain).GrainId;
        var activated = metrics.WaitForCount(InstrumentNames.CATALOG_ACTIVATION_LATENCY, id.Type.ToString(), 2);
        var calls = new List<Task>();
        ActivationData first;
        ActivationData second;
        GrainTypeSharedContext shared;
        StatelessWorkerGrainContext parent;
        try
        {
            // Do not issue the second request until the first worker is actually executing.
            // The call filter holds both requests without sleeps or a real-time delay.
            var firstReady = gate.Entered.Reader.ReadAsync(TestCancellation).AsTask();
            calls.Add(grain.Nop());
            first = await firstReady.WaitAsync(Timeout, TestCancellation);
            var secondReady = gate.Entered.Reader.ReadAsync(TestCancellation).AsTask();
            calls.Add(grain.Nop());
            second = await secondReady.WaitAsync(Timeout, TestCancellation);
            await activated;
            Assert.NotSame(first, second);
            Assert.Equal(first.GrainId, second.GrainId);
            Assert.NotEqual(first.ActivationId, second.ActivationId);
            Assert.NotSame(first.GrainInstance, second.GrainInstance);
            shared = AssertCanonicalContext(services, first);
            Assert.Same(shared, second.Shared);
            Assert.Same(shared.GrainTypeMetricName, second.Shared.GrainTypeMetricName);
            parent = Assert.IsType<StatelessWorkerGrainContext>(services.GetRequiredService<ActivationDirectory>().FindTarget(id));
            Assert.Null(parent.GrainInstance);
            Assert.NotSame(parent, first);
            Assert.NotSame(parent, second);
            AssertCounts(services, metrics, baseline, shared, 1, 2, 2);
            AssertActivationEvents(metrics, shared, created: 1, destroyed: 0, failed: 0, instances: [1, 1]);
            var latencies = metrics.For(InstrumentNames.CATALOG_ACTIVATION_LATENCY, shared.GrainTypeMetricName);
            Assert.Equal(2, latencies.Length);
            Assert.All(latencies, latency =>
            {
                AssertTypeTag(latency, shared, "directory", "grain_type", "status");
                Assert.Equal("disabled", latency.Tags["directory"]);
                Assert.Equal("success", latency.Tags["status"]);
            });
        }
        finally
        {
            gate.Release.TrySetResult();
            await Task.WhenAll(calls).WaitAsync(Timeout, TestCancellation);
        }

        var removed = metrics.WaitForCount(InstrumentNames.CATALOG_ACTIVATION_DESTROYED, shared.GrainTypeMetricName, 1);
        var parentCompleted = parent.Deactivated;
        parent.Deactivate(new(DeactivationReasonCode.RuntimeRequested, "stateless fanout cleanup"), TestCancellation);
        await Task.WhenAll(parentCompleted, first.Deactivated, second.Deactivated).WaitAsync(Timeout, TestCancellation);
        await removed;
        Assert.Null(services.GetRequiredService<ActivationDirectory>().FindTarget(id));
        AssertCounts(services, metrics, baseline, shared, 0, 0, 0);
        AssertActivationEvents(metrics, shared, created: 1, destroyed: 1, failed: 0, instances: [1, 1, -1, -1]);
        AssertDeactivationMetrics(metrics, shared, "deactivateOnIdle", "deactivateOnIdle");
        await parent.DisposeAsync().AsTask().WaitAsync(Timeout, TestCancellation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_NonExistentActivationReusesCachedType(bool metadataUnavailable)
    {
        await using var fixture = new MetricsFixture("non-existent");
        await fixture.InitializeAsync();
        var services = fixture.PrimaryServices;
        using var metrics = new RuntimeMetrics(services);
        using var events = new DiagnosticEventCollector(GrainLifecycleEvents.ListenerName);
        var baseline = metrics.Snapshot();
        var call = GetCall(fixture.GrainFactory, "named", 701);
        var activation = await InvokeAndObserve(services, metrics, events, call, 1);
        var shared = AssertCanonicalContext(services, activation);
        await Deactivate(activation);
        AssertCounts(services, metrics, baseline, shared, 0, 0, 0);

        using var rejected = new RuntimeMetrics(services);
        using var unavailable = services.GetRequiredService<CatalogAvailability>().RejectNewActivations(metadataUnavailable);
        var resolver = services.GetRequiredService<GrainPropertiesResolver>();
        Assert.Equal(!metadataUnavailable, resolver.TryGetGrainProperties(activation.GrainId.Type, out _));
        var instruments = services.GetRequiredService<CatalogInstruments>();
        Assert.True(instruments.NonExistentActivationsEnabled);
        Assert.True(instruments.TryGetGrainTypeMetrics(activation.GrainId.Type, out var cached));
        Assert.Same(shared.GrainTypeMetrics, cached);

        var catalog = services.GetRequiredService<Catalog>();
        Assert.Null(catalog.GetOrCreateActivation(activation.GrainId, null, null));
        Assert.Null(catalog.GetOrCreateActivation(GetCall(fixture.GrainFactory, "named", 702).Reference.GrainId, null, null));

        Assert.True(instruments.TryGetGrainTypeMetrics(activation.GrainId.Type, out var retained));
        Assert.Same(cached, retained);
        AssertNonExistentEvents(rejected, shared.GrainTypeMetricName, 2);
        AssertNonExistentGauges(services, rejected, baseline, cached);
        using var lateListener = new RuntimeMetrics(services);
        Assert.Empty(lateListener.Gauges(InstrumentNames.CATALOG_ACTIVATION_NON_EXISTENT_ACTIVATIONS));
        AssertNonExistentGauges(services, lateListener, baseline, cached);
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("named")]
    [InlineData("int")]
    [InlineData("string")]
    public async Task Runtime_NonExistentActivationCachesRecognizedType(string kind)
    {
        await using var fixture = new MetricsFixture("non-existent");
        await fixture.InitializeAsync();
        var services = fixture.PrimaryServices;
        using var metrics = new RuntimeMetrics(services);
        var baseline = metrics.Snapshot();
        var id = GetCall(fixture.GrainFactory, kind, 703).Reference.GrainId;
        var instruments = services.GetRequiredService<CatalogInstruments>();
        var resolver = services.GetRequiredService<GrainPropertiesResolver>();
        Assert.True(resolver.TryGetGrainProperties(id.Type, out _));
        Assert.False(instruments.TryGetGrainTypeMetrics(id.Type, out _));
        Assert.False(baseline.ContainsKey(id.Type.ToString()));
        Assert.True(instruments.NonExistentActivationsEnabled);
        using var unavailable = services.GetRequiredService<CatalogAvailability>().RejectNewActivations();

        var catalog = services.GetRequiredService<Catalog>();
        Assert.Null(catalog.GetOrCreateActivation(id, null, null));
        Assert.True(instruments.TryGetGrainTypeMetrics(id.Type, out var cached));
        Assert.Equal(id.Type.ToString(), cached.GrainTypeTagValue);
        Assert.Null(catalog.GetOrCreateActivation(GetCall(fixture.GrainFactory, kind, 704).Reference.GrainId, null, null));
        Assert.True(instruments.TryGetGrainTypeMetrics(id.Type, out var retained));
        Assert.Same(cached, retained);

        AssertNonExistentEvents(metrics, cached.GrainTypeTagValue, 2);
        AssertNonExistentGauges(services, metrics, baseline, cached);
        using var lateListener = new RuntimeMetrics(services);
        Assert.Empty(lateListener.Gauges(InstrumentNames.CATALOG_ACTIVATION_NON_EXISTENT_ACTIVATIONS));
        AssertNonExistentGauges(services, lateListener, baseline, cached);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_NonExistentActivationUsesUnknownWithoutCaching(bool metadataUnavailable)
    {
        await using var fixture = new MetricsFixture("non-existent");
        await fixture.InitializeAsync();
        var services = fixture.PrimaryServices;
        using var metrics = new RuntimeMetrics(services);
        var baseline = metrics.Snapshot();
        var instruments = services.GetRequiredService<CatalogInstruments>();
        var resolver = services.GetRequiredService<GrainPropertiesResolver>();
        var ids = metadataUnavailable
            ? new[] { "ordinary", "named", "int", "string" }.Select(kind => GetCall(fixture.GrainFactory, kind, 705).Reference.GrainId).ToArray()
            : Enumerable.Range(0, 4).Select(i => GrainId.Create($"unknown-metrics-type-{i}", "705")).ToArray();
        foreach (var id in ids)
        {
            Assert.Equal(metadataUnavailable, resolver.TryGetGrainProperties(id.Type, out _));
            Assert.False(instruments.TryGetGrainTypeMetrics(id.Type, out _));
        }

        using var unavailable = services.GetRequiredService<CatalogAvailability>().RejectNewActivations(metadataUnavailable);
        Assert.True(instruments.NonExistentActivationsEnabled);
        var catalog = services.GetRequiredService<Catalog>();
        foreach (var id in ids)
        {
            Assert.False(resolver.TryGetGrainProperties(id.Type, out _));
            Assert.Null(catalog.GetOrCreateActivation(id, null, null));
            Assert.Null(catalog.GetOrCreateActivation(id, null, null));
            Assert.False(instruments.TryGetGrainTypeMetrics(id.Type, out _));
            Assert.Empty(metrics.ForType(id.Type.ToString()));
        }

        Assert.False(instruments.TryGetGrainTypeMetrics(GrainType.Create(GrainTypeMetrics.UnknownGrainType), out _));
        AssertNonExistentEvents(metrics, GrainTypeMetrics.UnknownGrainType, ids.Length * 2);
        AssertNonExistentGauges(services, metrics, baseline);
        using var lateListener = new RuntimeMetrics(services);
        AssertNonExistentGauges(services, lateListener, baseline);
    }

    [Fact]
    public async Task Runtime_NonExistentActivationDisabledDoesNotPopulateCache()
    {
        await using var fixture = new MetricsFixture("non-existent");
        await fixture.InitializeAsync();
        var services = fixture.PrimaryServices;
        using var metrics = new RuntimeMetrics(services, nonExistentActivationsEnabled: false);
        using var events = new DiagnosticEventCollector(GrainLifecycleEvents.ListenerName);
        var baseline = metrics.Snapshot();
        var activation = await InvokeAndObserve(services, metrics, events, GetCall(fixture.GrainFactory, "named", 706), 1);
        var cached = AssertCanonicalContext(services, activation).GrainTypeMetrics;
        await Deactivate(activation);
        var instruments = services.GetRequiredService<CatalogInstruments>();
        Assert.False(instruments.NonExistentActivationsEnabled);
        var ids = new[] { "ordinary", "int", "string" }
            .Select(kind => GetCall(fixture.GrainFactory, kind, 707).Reference.GrainId)
            .Concat(Enumerable.Range(0, 4).Select(i => GrainId.Create($"disabled-metrics-type-{i}", "707")))
            .ToArray();
        var resolver = services.GetRequiredService<GrainPropertiesResolver>();
        for (var i = 0; i < ids.Length; i++)
        {
            Assert.Equal(i < 3, resolver.TryGetGrainProperties(ids[i].Type, out _));
            Assert.False(instruments.TryGetGrainTypeMetrics(ids[i].Type, out _));
        }

        var availability = services.GetRequiredService<CatalogAvailability>();
        using var unavailable = availability.RejectNewActivations();
        // Cache invalidation has its own directory-resolution work even when instrumentation
        // is disabled. Warm that unrelated path before measuring metric-only resolution.
        var locator = services.GetRequiredService<GrainLocator>();
        foreach (var id in ids)
        {
            locator.InvalidateCache(id);
        }

        var manifestReads = availability.ManifestReads;
        var catalog = services.GetRequiredService<Catalog>();
        Assert.Null(catalog.GetOrCreateActivation(activation.GrainId, null, null));
        foreach (var id in ids)
        {
            Assert.Null(catalog.GetOrCreateActivation(id, null, null));
            Assert.Null(catalog.GetOrCreateActivation(id, null, null));
            Assert.False(instruments.TryGetGrainTypeMetrics(id.Type, out _));
            Assert.Empty(metrics.ForType(id.Type.ToString()));
        }

        Assert.Equal(manifestReads, availability.ManifestReads);
        Assert.True(instruments.TryGetGrainTypeMetrics(activation.GrainId.Type, out var retained));
        Assert.Same(cached, retained);
        Assert.False(instruments.TryGetGrainTypeMetrics(GrainType.Create(GrainTypeMetrics.UnknownGrainType), out _));
        Assert.Empty(metrics.Gauges(InstrumentNames.CATALOG_ACTIVATION_NON_EXISTENT_ACTIVATIONS));
        AssertNonExistentGauges(services, metrics, baseline, cached);
        using var lateListener = new RuntimeMetrics(services);
        AssertNonExistentGauges(services, lateListener, baseline, cached);
    }

    private static void AssertNonExistentEvents(RuntimeMetrics metrics, string type, int count)
    {
        var samples = metrics.Gauges(InstrumentNames.CATALOG_ACTIVATION_NON_EXISTENT_ACTIVATIONS);
        Assert.Equal(count, samples.Length);
        Assert.All(samples, sample =>
        {
            Assert.IsType<Counter<int>>(sample.Instrument);
            Assert.Equal(1, sample.Value);
            Assert.Equal("grain_type", Assert.Single(sample.Tags).Key);
            Assert.Same(type, sample.Tags["grain_type"]);
        });
        Assert.All(metrics.ForType(type), sample => Assert.Equal(InstrumentNames.CATALOG_ACTIVATION_NON_EXISTENT_ACTIVATIONS, sample.Instrument.Name));
    }

    private static void AssertNonExistentGauges(IServiceProvider services, RuntimeMetrics metrics,
        Dictionary<string, int> baseline, params GrainTypeMetrics[] retained)
    {
        var expected = new Dictionary<string, int>(baseline);
        foreach (var type in retained)
        {
            expected[type.GrainTypeTagValue] = 0;
        }

        for (var scrape = 0; scrape < 2; scrape++)
        {
            var snapshot = metrics.Snapshot();
            Assert.Equal(expected.OrderBy(entry => entry.Key), snapshot.OrderBy(entry => entry.Key));
            Assert.Equal(services.GetRequiredService<ActivationDirectory>().Count, snapshot.Values.Sum());
            Assert.Equal(services.GetRequiredService<ActivationWorkingSet>().Count,
                metrics.Gauges(InstrumentNames.CATALOG_ACTIVATION_WORKING_SET).Sum(sample => sample.Value));
            Assert.Equal(expected.Keys.Order(),
                metrics.Gauges(InstrumentNames.CATALOG_ACTIVATION_WORKING_SET).Select(sample => Assert.IsType<string>(sample.Tags["grain_type"])).Order());
            foreach (var type in retained)
            {
                foreach (var instrument in new[] { InstrumentNames.CATALOG_ACTIVATION_COUNT, InstrumentNames.CATALOG_ACTIVATION_WORKING_SET })
                {
                    var sample = Assert.Single(metrics.For(instrument, type.GrainTypeTagValue));
                    Assert.Equal(0, sample.Value);
                    Assert.Same(type.GrainTypeTagValue, sample.Tags["grain_type"]);
                }
            }
        }
    }

    private static GrainTypeSharedContext AssertCanonicalContext(IServiceProvider services, ActivationData activation)
    {
        Assert.True(services.GetRequiredService<GrainClassMap>().TryGetGrainClass(activation.GrainId.Type, out var implementation));
        var canonical = services.GetRequiredService<GrainTypeResolver>().GetGrainType(implementation);
        Assert.Equal(canonical, activation.GrainId.Type);
        var shared = services.GetRequiredService<GrainTypeSharedContextResolver>().GetComponents(canonical);
        Assert.Same(shared, activation.Shared);
        Assert.Same(services.GetRequiredService<CatalogInstruments>().GetGrainTypeMetrics(canonical), shared.GrainTypeMetrics);
        Assert.Equal(canonical.ToString(), shared.GrainTypeMetricName);
        Assert.NotEqual(shared.GrainTypeName, shared.GrainTypeMetricName);
        return shared;
    }

    private static (GrainReference Reference, Func<Task> Invoke) GetCall(IGrainFactory factory, string kind, int key)
    {
        switch (kind)
        {
            case "ordinary":
                var idle = factory.GetGrain<IIdleActivationGcTestGrain1>(new Guid(key, 0, 0, new byte[8]));
                return ((GrainReference)idle, idle.Nop);
            case "named":
                var guidKey = new Guid(key, 0, 0, new byte[8]);
                var named = factory.GetGrain<IGuidTestGrain>(guidKey);
                return ((GrainReference)named, async () => Assert.Equal(guidKey, await named.GetKey()));
            case "int":
                var integer = factory.GetGrain<IGenericExtensionTestGrain<int>>(key);
                return ((GrainReference)integer, () => integer.InstallExtension(17));
            case "string":
                var text = factory.GetGrain<IGenericExtensionTestGrain<string>>(key);
                return ((GrainReference)text, () => text.InstallExtension("runtime-metrics"));
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static async Task<ActivationData> InvokeAndObserve(
        IServiceProvider services, RuntimeMetrics metrics, DiagnosticEventCollector events,
        (GrainReference Reference, Func<Task> Invoke) call, int activationCount, ActivationData? previous = null)
    {
        var ready = events.WaitForEventAsync(nameof(GrainLifecycleEvents.Activated),
            e => e.Payload is GrainLifecycleEvents.Activated a && a.GrainContext.GrainId == call.Reference.GrainId
                && !ReferenceEquals(a.GrainContext, previous), Timeout, TestCancellation);
        var recorded = metrics.WaitForCount(InstrumentNames.CATALOG_ACTIVATION_LATENCY, call.Reference.GrainId.Type.ToString(), activationCount);
        await call.Invoke().WaitAsync(Timeout, TestCancellation);
        await ready;
        await recorded;
        var activation = Assert.IsType<ActivationData>(services.GetRequiredService<ActivationDirectory>().FindTarget(call.Reference.GrainId));
        Assert.Equal(ActivationState.Valid, activation.State);
        return activation;
    }

    private static async Task Deactivate(ActivationData activation)
    {
        var completed = activation.Deactivated;
        activation.Deactivate(new(DeactivationReasonCode.RuntimeRequested, "runtime metrics cleanup"), TestCancellation);
        await completed.WaitAsync(Timeout, TestCancellation);
    }

    private static void AssertCounts(IServiceProvider services, RuntimeMetrics metrics, Dictionary<string, int> baseline,
        GrainTypeSharedContext shared, int directoryCount, int workingSetCount, long instanceCount)
    {
        var snapshot = metrics.Snapshot();
        var directory = services.GetRequiredService<ActivationDirectory>();
        var workingSet = services.GetRequiredService<ActivationWorkingSet>();
        var name = shared.GrainTypeMetricName;
        Assert.Equal(directoryCount, snapshot[name]);
        Assert.Equal(workingSetCount, metrics.Gauge(InstrumentNames.CATALOG_ACTIVATION_WORKING_SET, name));
        Assert.Equal(directoryCount, directory.Count(entry => entry.Key.Type.ToString() == name));
        Assert.Equal(directory.Count, snapshot.Values.Sum());
        Assert.Equal(workingSet.Count, metrics.Gauges(InstrumentNames.CATALOG_ACTIVATION_WORKING_SET).Sum(s => s.Value));
        // System targets are included in directory totals but not in grain-object totals.
        foreach (var entry in baseline)
        {
            Assert.Equal(entry.Value, snapshot[entry.Key]);
        }

        var statistics = new GrainCountStatistics(services.GetRequiredService<GrainInstruments>()).GetSimpleGrainStatistics().ToDictionary();
        if (instanceCount == 0)
        {
            Assert.False(statistics.ContainsKey(shared.GrainTypeName));
        }
        else
        {
            Assert.Equal(instanceCount, statistics[shared.GrainTypeName]);
        }

        Assert.Same(name, Assert.Single(metrics.Gauges(InstrumentNames.CATALOG_ACTIVATION_COUNT), s => Equals(s.Tags["grain_type"], name)).Tags["grain_type"]);
        Assert.Same(name, Assert.Single(metrics.Gauges(InstrumentNames.CATALOG_ACTIVATION_WORKING_SET), s => Equals(s.Tags["grain_type"], name)).Tags["grain_type"]);
    }

    private static void AssertActivationEvents(RuntimeMetrics metrics, GrainTypeSharedContext shared,
        int created, int destroyed, int failed, double[] instances, int concurrent = 0)
    {
        AssertCounter(InstrumentNames.CATALOG_ACTIVATION_CREATED, created);
        AssertCounter(InstrumentNames.CATALOG_ACTIVATION_DESTROYED, destroyed);
        AssertCounter(InstrumentNames.CATALOG_ACTIVATION_FAILED_TO_ACTIVATE, failed);
        AssertCounter(InstrumentNames.CATALOG_ACTIVATION_CONCURRENT_REGISTRATION_ATTEMPTS, concurrent);
        var grainEvents = metrics.For(InstrumentNames.GRAIN_COUNTS, shared.GrainTypeMetricName);
        Assert.Equal(instances, grainEvents.Select(sample => sample.Value));
        Assert.All(grainEvents, sample => AssertTypeTag(sample, shared, "grain_type"));

        void AssertCounter(string instrument, int count)
        {
            var samples = metrics.For(instrument, shared.GrainTypeMetricName);
            Assert.Equal(count, samples.Length);
            Assert.All(samples, sample =>
            {
                Assert.Equal(1, sample.Value);
                AssertTypeTag(sample, shared, "grain_type");
            });
        }
    }

    private static void AssertActivationLatencies(RuntimeMetrics metrics, GrainTypeSharedContext shared, params string[] statuses)
    {
        var samples = metrics.For(InstrumentNames.CATALOG_ACTIVATION_LATENCY, shared.GrainTypeMetricName);
        Assert.Equal(statuses, samples.Select(s => s.Tags["status"]));
        Assert.All(samples, sample =>
        {
            AssertTypeTag(sample, shared, "directory", "grain_type", "status");
            Assert.Equal("enabled", sample.Tags["directory"]);
            Assert.Equal("ms", sample.Instrument.Unit);
            Assert.True(double.IsFinite(sample.Value) && sample.Value >= 0, $"Invalid activation latency {sample.Value}.");
        });
    }

    private static void AssertDeactivationMetrics(RuntimeMetrics metrics, GrainTypeSharedContext shared, params string[] vias)
    {
        var shutdowns = metrics.For(InstrumentNames.CATALOG_ACTIVATION_SHUTDOWN, shared.GrainTypeMetricName);
        var latencies = metrics.For(InstrumentNames.CATALOG_DEACTIVATION_LATENCY, shared.GrainTypeMetricName);
        Assert.Equal(vias, shutdowns.Select(s => s.Tags["via"]));
        Assert.Equal(vias, latencies.Select(s => s.Tags["via"]));
        Assert.All(shutdowns, sample =>
        {
            Assert.Equal(1, sample.Value);
            AssertTypeTag(sample, shared, "grain_type", "via");
        });
        Assert.All(latencies, sample =>
        {
            AssertTypeTag(sample, shared, "grain_type", "via");
            Assert.Equal("ms", sample.Instrument.Unit);
            Assert.True(double.IsFinite(sample.Value) && sample.Value >= 0, $"Invalid deactivation latency {sample.Value}.");
        });
    }

    private static void AssertTypeTag(MetricSample sample, GrainTypeSharedContext shared, params string[] keys)
    {
        Assert.Equal(keys, sample.Tags.Keys.Order());
        Assert.Same(shared.GrainTypeMetricName, sample.Tags["grain_type"]);
    }

    private sealed record MetricSample(Instrument Instrument, double Value, Dictionary<string, object?> Tags);

    private sealed class RuntimeMetrics : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly object _lock = new();
        private readonly List<MetricSample> _samples = [];
        private readonly List<(string Instrument, string Type, int Count, TaskCompletionSource Completion)> _waiters = [];

        public RuntimeMetrics(IServiceProvider services, bool nonExistentActivationsEnabled = true)
        {
            var meter = services.GetRequiredService<OrleansInstruments>().Meter;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter)
                    && (instrument.Name.StartsWith("orleans-catalog-", StringComparison.Ordinal) || instrument.Name == InstrumentNames.GRAIN_COUNTS)
                    && (nonExistentActivationsEnabled || instrument.Name != InstrumentNames.CATALOG_ACTIVATION_NON_EXISTENT_ACTIVATIONS))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.Start();
        }

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            lock (_lock)
            {
                var sample = new MetricSample(instrument, value, tags.ToArray().ToDictionary());
                _samples.Add(sample);
                foreach (var waiter in _waiters)
                {
                    if (For(waiter.Instrument, waiter.Type).Length >= waiter.Count)
                    {
                        waiter.Completion.TrySetResult();
                    }
                }
            }
        }

        public Task WaitForCount(string instrument, string type, int count)
        {
            lock (_lock)
            {
                if (For(instrument, type).Length >= count) return Task.CompletedTask;
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((instrument, type, count, completion));
                return Wait();

                async Task Wait()
                {
                    try
                    {
                        await completion.Task.WaitAsync(Timeout, TestCancellation);
                    }
                    catch (TimeoutException exception)
                    {
                        throw new TimeoutException($"Waiting for {instrument}, grain_type={type}: expected {count}, observed {For(instrument, type).Length}.", exception);
                    }
                }
            }
        }

        public MetricSample[] For(string instrument, string type)
        {
            lock (_lock)
            {
                return _samples.Where(s => s.Instrument.Name == instrument && s.Tags.TryGetValue("grain_type", out var value) && Equals(value, type)).ToArray();
            }
        }

        public MetricSample[] ForType(string type)
        {
            lock (_lock)
            {
                return _samples.Where(s => s.Tags.TryGetValue("grain_type", out var value) && Equals(value, type)).ToArray();
            }
        }

        public MetricSample[] Gauges(string instrument)
        {
            lock (_lock) return _samples.Where(s => s.Instrument.Name == instrument).ToArray();
        }

        public double Gauge(string instrument, string type) => Assert.Single(For(instrument, type)).Value;

        public Dictionary<string, int> Snapshot()
        {
            lock (_lock)
            {
                _samples.RemoveAll(s => s.Instrument.Name is InstrumentNames.CATALOG_ACTIVATION_COUNT or InstrumentNames.CATALOG_ACTIVATION_WORKING_SET);
                _listener.RecordObservableInstruments();
                // ToDictionary also rejects duplicate buckets within a single scrape.
                return Gauges(InstrumentNames.CATALOG_ACTIVATION_COUNT).ToDictionary(s => Assert.IsType<string>(s.Tags["grain_type"]), s => checked((int)s.Value));
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class MetricsFixture(string mode = "normal", short siloCount = 1) : BaseTestClusterFixture
    {
        public IServiceProvider PrimaryServices => ((InProcessSiloHandle)HostedCluster.Primary!).SiloHost.Services;

        public override async ValueTask DisposeAsync()
        {
            foreach (var silo in HostedCluster.Silos.Cast<InProcessSiloHandle>())
            {
                var control = silo.SiloHost.Services.GetRequiredService<LifecycleControl>();
                control.ActivationRelease.TrySetResult();
                control.DisposalRelease.TrySetResult();
                control.DirectoryResponse.TrySetResult(null);
                silo.SiloHost.Services.GetService<WorkerCallGate>()?.Release.TrySetResult();
            }

            await base.DisposeAsync();
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = siloCount;
            builder.Properties["RuntimeMetricsMode"] = mode;
            builder.AddSiloBuilderConfigurator<MetricsConfigurator>();
        }
    }

    public sealed class MetricsConfigurator : IHostConfigurator
    {
        public void Configure(IHostBuilder hostBuilder) => hostBuilder.ConfigureServices((context, services) =>
        {
            // Freeze maintenance, not membership/networking. No working-set scan or collection
            // can race the quiescent metric snapshots, even on a slow test machine.
            services.AddKeyedSingleton<TimeProvider>(TimeProviderNames.SystemTimers, new FakeTimeProvider());
            services.AddKeyedSingleton<TimeProvider>(TimeProviderNames.ActivationManagement, new FakeTimeProvider());
            services.AddSingleton(new LifecycleControl(context.Configuration["RuntimeMetricsMode"]!));
            services.AddScoped<ScopeSentinel>();
            services.AddSingleton<IConfigureGrainContextProvider, LifecycleConfigurator>();
            services.AddSingleton<IConfigureGrainTypeComponents, LifecycleConfigurator>();
            services.AddSingleton<ControlledDirectory>();
            services.AddSingleton<IGrainDirectoryResolver, ControlledDirectoryResolver>();
            if (context.Configuration["RuntimeMetricsMode"] == "stateless-fanout")
            {
                services.AddSingleton<WorkerCallGate>();
                services.AddSingleton<IIncomingGrainCallFilter>(provider => provider.GetRequiredService<WorkerCallGate>());
                services.AddSingleton<IGrainPropertiesProvider, WorkerProperties>();
            }

            if (context.Configuration["RuntimeMetricsMode"] == "non-existent")
            {
                services.AddSingleton<CatalogAvailability>();
                services.AddSingleton<ISiloStatusOracle>(provider => provider.GetRequiredService<CatalogAvailability>());
                services.AddSingleton(provider => new GrainPropertiesResolver(provider.GetRequiredService<CatalogAvailability>()));
            }
        });
    }

    private sealed class WorkerProperties : IGrainPropertiesProvider
    {
        public void Populate(Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            if (grainClass == typeof(UnitTests.Grains.StatelessWorkerActivationCollectorTestGrain1))
            {
                // A deterministic two-worker limit, including on single-CPU test runners.
                properties["max-local-instances"] = "2";
                properties["remove-idle-workers"] = "false";
            }
        }
    }

    private sealed class WorkerCallGate : IIncomingGrainCallFilter
    {
        public Channel<ActivationData> Entered { get; } = Channel.CreateUnbounded<ActivationData>();
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task Invoke(IIncomingGrainCallContext context)
        {
            if (context.Grain is UnitTests.Grains.StatelessWorkerActivationCollectorTestGrain1
                && context.MethodName == nameof(IStatelessWorkerActivationCollectorTestGrain1.Nop))
            {
                await Entered.Writer.WriteAsync(Assert.IsType<ActivationData>(context.TargetContext));
                await Release.Task;
            }

            await context.Invoke();
        }
    }

    // Only Catalog's availability inputs are controlled. Membership continues running,
    // and the actual GrainPropertiesResolver reads the real manifest unless it is withheld.
    private sealed class CatalogAvailability(SiloStatusOracle oracle, IClusterManifestProvider manifest) :
        ISiloStatusOracle, IClusterManifestProvider, IDisposable
    {
        private bool _rejectActivations;
        private bool _metadataUnavailable;
        private int _manifestReads;

        public int ManifestReads => Volatile.Read(ref _manifestReads);

        public IDisposable RejectNewActivations(bool metadataUnavailable = false)
        {
            Assert.Equal(SiloStatus.Active, CurrentStatus);
            _rejectActivations = true;
            _metadataUnavailable = metadataUnavailable;
            return this;
        }

        public void Dispose()
        {
            _metadataUnavailable = false;
            _rejectActivations = false;
        }

        public ClusterManifest Current
        {
            get
            {
                Interlocked.Increment(ref _manifestReads);
                return _metadataUnavailable ? null! : manifest.Current;
            }
        }
        public IAsyncEnumerable<ClusterManifest> Updates => manifest.Updates;
        public GrainManifest LocalGrainManifest => manifest.LocalGrainManifest;
        public SiloStatus CurrentStatus => _rejectActivations ? SiloStatus.ShuttingDown : oracle.CurrentStatus;
        public string SiloName => oracle.SiloName;
        public SiloAddress SiloAddress => oracle.SiloAddress;
        public SiloAddress[] GetActiveSilos() => oracle.GetActiveSilos();
        public SiloStatus GetApproximateSiloStatus(SiloAddress siloAddress) => oracle.GetApproximateSiloStatus(siloAddress);
        public Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false) => oracle.GetApproximateSiloStatuses(onlyActive);
        public bool TryGetSiloName(SiloAddress siloAddress, [NotNullWhen(true)] out string? siloName) => oracle.TryGetSiloName(siloAddress, out siloName);
        public bool IsFunctionalDirectory(SiloAddress siloAddress) => oracle.IsFunctionalDirectory(siloAddress);
        public bool IsDeadSilo(SiloAddress silo) => oracle.IsDeadSilo(silo);
        public bool SubscribeToSiloStatusEvents(ISiloStatusListener observer) => oracle.SubscribeToSiloStatusEvents(observer);
        public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener observer) => oracle.UnSubscribeFromSiloStatusEvents(observer);
    }

    private sealed class LifecycleControl(string mode) : IActivationLifecycleObserver
    {
        public string Mode { get; } = mode;
        public InvalidOperationException Failure { get; } = new("Injected runtime metrics lifecycle failure.");
        public ActivationData? Captured;
        public CancellationToken ActivationToken;
        public TaskCompletionSource ActivationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ActivationRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ActivationExited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ObserverCreated;
        public int ObserverDestroyed;
        public int DisposeInvocations;
        public int ScopeDisposed;
        public int DirectoryRegistrations;
        public CancellationToken DirectoryToken;
        public TaskCompletionSource<GrainAddress> DirectoryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<GrainAddress?> DirectoryResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnCreateActivation(IGrainContext grainContext) => Interlocked.Increment(ref ObserverCreated);
        public void OnDestroyActivation(IGrainContext grainContext) => Interlocked.Increment(ref ObserverDestroyed);
    }

    private sealed class ScopeSentinel(LifecycleControl control) : IDisposable
    {
        public void Dispose() => Interlocked.Increment(ref control.ScopeDisposed);
    }

    private sealed class LifecycleConfigurator(LifecycleControl control, GrainTypeResolver resolver) :
        IConfigureGrainContextProvider, IConfigureGrainContext, IConfigureGrainTypeComponents
    {
        private readonly GrainType _target = resolver.GetGrainType(typeof(UnitTests.Grains.IdleActivationGcTestGrain1));

        public bool TryGetConfigurator(GrainType grainType, GrainProperties properties, [NotNullWhen(true)] out IConfigureGrainContext? configurator)
        {
            configurator = grainType == _target && control.Mode != "normal" ? this : null;
            return configurator is not null;
        }

        public void Configure(IGrainContext context)
        {
            control.Captured = Assert.IsType<ActivationData>(context);
            context.SetComponent<IActivationLifecycleObserver>(control);
            _ = context.ActivationServices.GetRequiredService<ScopeSentinel>();
        }

        public void Configure(GrainType grainType, GrainProperties properties, GrainTypeSharedContext shared)
        {
            if (grainType == _target && control.Mode != "normal")
            {
                shared.SetComponent<IGrainActivator>(new ControlledActivator(shared.GetComponent<IGrainActivator>()!, control));
            }
        }
    }

    private sealed class ControlledActivator(IGrainActivator inner, LifecycleControl control) : IGrainActivator
    {
        public object CreateInstance(IGrainContext context)
        {
            if (control.Mode == "constructor-error") throw control.Failure;
            return control.Mode.StartsWith("activation-", StringComparison.Ordinal) ? new ActivationProbe(context, control) : inner.CreateInstance(context);
        }

        public async ValueTask DisposeInstance(IGrainContext context, object instance)
        {
            Interlocked.Increment(ref control.DisposeInvocations);
            if (control.Mode.StartsWith("dispose-", StringComparison.Ordinal))
            {
                control.DisposalEntered.TrySetResult();
                await control.DisposalRelease.Task;
            }

            if (instance is not ActivationProbe) await inner.DisposeInstance(context, instance);
            if (control.Mode == "dispose-object-disposed") throw new ObjectDisposedException("test grain");
            if (control.Mode == "dispose-error") throw control.Failure;
        }
    }

    // This is an activator seam, not a new grain implementation or a fake activation context.
    // ActivationData calls this object's OnActivateAsync using its actual runtime token.
    private sealed class ControlledDirectoryResolver(ControlledDirectory directory, LifecycleControl control, GrainTypeResolver resolver) : IGrainDirectoryResolver
    {
        private readonly GrainType _target = resolver.GetGrainType(typeof(UnitTests.Grains.IdleActivationGcTestGrain1));

        public bool TryResolveGrainDirectory(GrainType grainType, GrainProperties? properties, [NotNullWhen(true)] out IGrainDirectory? grainDirectory)
        {
            grainDirectory = grainType == _target && control.Mode.StartsWith("directory-", StringComparison.Ordinal) ? directory : null;
            return grainDirectory is not null;
        }
    }

    private sealed class ControlledDirectory(LifecycleControl control) : IGrainDirectory
    {
        public Task<GrainAddress?> Register(GrainAddress address) => Register(address, null, CancellationToken.None);

        public Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref control.DirectoryRegistrations);
            control.DirectoryToken = cancellationToken;
            control.DirectoryEntered.TrySetResult(address);
            return control.DirectoryResponse.Task;
        }

        public Task<GrainAddress?> Lookup(GrainId grainId) => Task.FromResult<GrainAddress?>(null);
        public Task Unregister(GrainAddress address) => Task.CompletedTask;
        public Task UnregisterSilos(List<SiloAddress> siloAddresses) => Task.CompletedTask;
    }

    private sealed class ActivationProbe(IGrainContext context, LifecycleControl control) : IGrainBase
    {
        public IGrainContext GrainContext => context;

        public async Task OnActivateAsync(CancellationToken token)
        {
            control.ActivationToken = token;
            control.ActivationEntered.TrySetResult();
            try
            {
                await control.ActivationRelease.Task.WaitAsync(token);
                throw control.Failure;
            }
            finally
            {
                control.ActivationExited.TrySetResult();
            }
        }
    }
}

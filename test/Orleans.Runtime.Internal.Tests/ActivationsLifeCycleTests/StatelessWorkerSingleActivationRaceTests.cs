#nullable enable
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Runtime.Diagnostics;
using Orleans.Serialization.Invocation;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

/// <summary>
/// Regression tests for a race in <c>StatelessWorkerGrainContext</c> where the work loop
/// could create a "phantom" worker on a context that had already been unregistered from
/// the catalog, allowing more than the configured <see cref="StatelessWorkerAttribute.MaxLocalWorkers"/>
/// activations to coexist on a single silo for the same grain id.
/// </summary>
public class StatelessWorkerSingleActivationRaceTests(StatelessWorkerSingleActivationRaceTests.Fixture fixture)
        : IClassFixture<StatelessWorkerSingleActivationRaceTests.Fixture>
{
    public class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.Services.AddSingleton<StatelessWorkerSingleActivationTracker>();
                hostBuilder.Services.Configure<StatelessWorkerOptions>(options =>
                {
                    options.RemoveIdleWorkers = true;
                    options.IdleWorkersInspectionPeriod = TimeSpan.FromMilliseconds(5);
                    options.MinIdleCyclesBeforeRemoval = 1;
                });
            }
        }
    }

    private readonly Fixture _fixture = fixture;

    private InProcessSiloHandle PrimarySilo => (InProcessSiloHandle)_fixture.HostedCluster.Primary;

    /// <summary>
    /// Deterministically verifies that once the last worker of a stateless worker context is
    /// destroyed, the context is marked terminated, is removed from the activation directory,
    /// and forwards a late delivery to a fresh wrapper instead of resurrecting a worker.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("StatelessWorker")]
    public async Task TerminatedContextForwardsLateDeliveryToFreshWrapper()
    {
        using var collector = new DiagnosticEventCollector(StatelessWorkerEvents.ListenerName);
        var directory = PrimarySilo.SiloHost.Services.GetRequiredService<ActivationDirectory>();

        var grain = _fixture.GrainFactory.GetGrain<IStatelessWorkerSingleActivationGrain>(7654321);
        var grainId = ((GrainReference)grain).GrainId;

        await grain.DoWork();

        var c1 = Assert.IsType<StatelessWorkerGrainContext>(directory.FindTarget(grainId));
        collector.Clear();

        c1.Deactivate(new DeactivationReason(DeactivationReasonCode.RuntimeRequested, "test"), CancellationToken.None);

        var terminated = await WaitForContextTerminatedAsync(collector, c1);
        Assert.Equal(0, terminated.WorkerCount);
        Assert.Null(directory.FindTarget(grainId));

        collector.Clear();

        c1.ReceiveMessage(CreateLateDeliveryMessage(grainId));

        var forwarded = await WaitForMessageForwardedAsync(collector, c1);
        var workerCreated = await WaitForWorkerCreatedAsync(collector, forwarded.ReplacementContext);

        Assert.NotSame(c1, forwarded.ReplacementContext);
        Assert.Same(forwarded.ReplacementContext, directory.FindTarget(grainId));
        Assert.Same(forwarded.ReplacementContext, workerCreated.Context);
        Assert.Empty(GetWorkerCreatedEvents(collector, c1));
    }

    /// <summary>
    /// Deterministically exercises the original race by combining a late delivery to an
    /// orphaned wrapper with a fresh grain call through the directory. Both operations
    /// must use the same replacement activation so that <c>maxLocalWorkers: 1</c> is
    /// preserved even when a terminated wrapper receives another message.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("StatelessWorker")]
    public async Task LateDeliveryAndFreshCallUseSingleReplacementActivation()
    {
        using var collector = new DiagnosticEventCollector(StatelessWorkerEvents.ListenerName);
        var tracker = PrimarySilo.SiloHost.Services.GetRequiredService<StatelessWorkerSingleActivationTracker>();
        var directory = PrimarySilo.SiloHost.Services.GetRequiredService<ActivationDirectory>();

        var grain = _fixture.GrainFactory.GetGrain<IStatelessWorkerSingleActivationGrain>(424242);
        var grainId = ((GrainReference)grain).GrainId;

        tracker.Reset();
        await grain.DoWork();

        var c1 = Assert.IsType<StatelessWorkerGrainContext>(directory.FindTarget(grainId));
        collector.Clear();

        c1.Deactivate(new DeactivationReason(DeactivationReasonCode.RuntimeRequested, "test"), CancellationToken.None);
        _ = await WaitForContextTerminatedAsync(collector, c1);

        tracker.Reset();
        collector.Clear();

        var freshCallTask = grain.DoWork();
        c1.ReceiveMessage(CreateLateDeliveryMessage(grainId));
        await freshCallTask;

        var forwarded = await WaitForMessageForwardedAsync(collector, c1);
        _ = await WaitForWorkerCreatedAsync(collector, forwarded.ReplacementContext);

        var workerCreatedEvents = GetWorkerCreatedEvents(collector);
        var replacementCreations = Assert.Single(workerCreatedEvents);

        Assert.NotSame(c1, forwarded.ReplacementContext);
        Assert.Same(forwarded.ReplacementContext, replacementCreations.Context);
        Assert.Empty(GetWorkerCreatedEvents(collector, c1));
        Assert.True(
            tracker.MaxObserved <= 1,
            $"Observed up to {tracker.MaxObserved} concurrent activations after a late delivery and a fresh call on a [StatelessWorker(maxLocalWorkers:1)] grain; expected at most 1.");
    }

    private Message CreateLateDeliveryMessage(GrainId grainId)
    {
        var services = PrimarySilo.SiloHost.Services;
        var messageFactory = services.GetRequiredService<MessageFactory>();
        var localSiloDetails = services.GetRequiredService<ILocalSiloDetails>();

        var message = messageFactory.CreateMessage(new DoWorkRequest(), InvokeMethodOptions.OneWay);
        message.SetInfiniteTimeToLive();
        message.IsLocalOnly = true;
        message.SendingGrain = grainId;
        message.TargetGrain = grainId;
        message.SendingSilo = localSiloDetails.SiloAddress;
        message.TargetSilo = localSiloDetails.SiloAddress;
        return message;
    }

    private static IReadOnlyList<StatelessWorkerEvents.WorkerCreated> GetWorkerCreatedEvents(DiagnosticEventCollector collector)
    {
        return [.. collector.Events
            .Select(static evt => evt.Payload)
            .OfType<StatelessWorkerEvents.WorkerCreated>()];
    }

    private static IReadOnlyList<StatelessWorkerEvents.WorkerCreated> GetWorkerCreatedEvents(DiagnosticEventCollector collector, IGrainContext context)
    {
        return [.. GetWorkerCreatedEvents(collector)
            .Where(evt => ReferenceEquals(evt.Context, context))];
    }

    private static async Task<StatelessWorkerEvents.ContextTerminated> WaitForContextTerminatedAsync(
        DiagnosticEventCollector collector,
        IGrainContext context)
    {
        var evt = await collector.WaitForEventAsync(
            nameof(StatelessWorkerEvents.ContextTerminated),
            diagnosticEvent => diagnosticEvent.Payload is StatelessWorkerEvents.ContextTerminated terminated
                && ReferenceEquals(terminated.Context, context),
            TimeSpan.FromSeconds(10));
        return Assert.IsType<StatelessWorkerEvents.ContextTerminated>(evt.Payload);
    }

    private static async Task<StatelessWorkerEvents.MessageForwarded> WaitForMessageForwardedAsync(
        DiagnosticEventCollector collector,
        IGrainContext context)
    {
        var evt = await collector.WaitForEventAsync(
            nameof(StatelessWorkerEvents.MessageForwarded),
            diagnosticEvent => diagnosticEvent.Payload is StatelessWorkerEvents.MessageForwarded forwarded
                && ReferenceEquals(forwarded.Context, context),
            TimeSpan.FromSeconds(10));
        return Assert.IsType<StatelessWorkerEvents.MessageForwarded>(evt.Payload);
    }

    private static async Task<StatelessWorkerEvents.WorkerCreated> WaitForWorkerCreatedAsync(
        DiagnosticEventCollector collector,
        IGrainContext context)
    {
        var evt = await collector.WaitForEventAsync(
            nameof(StatelessWorkerEvents.WorkerCreated),
            diagnosticEvent => diagnosticEvent.Payload is StatelessWorkerEvents.WorkerCreated workerCreated
                && ReferenceEquals(workerCreated.Context, context),
            TimeSpan.FromSeconds(10));
        return Assert.IsType<StatelessWorkerEvents.WorkerCreated>(evt.Payload);
    }

    private sealed class DoWorkRequest : IInvokable
    {
        private static readonly MethodInfo Method = typeof(IStatelessWorkerSingleActivationGrain).GetMethod(nameof(IStatelessWorkerSingleActivationGrain.DoWork))!;
        private IStatelessWorkerSingleActivationGrain? _target;

        public object? GetTarget() => _target;

        public void SetTarget(ITargetHolder holder)
        {
            _target = (IStatelessWorkerSingleActivationGrain)holder.GetTarget()!;
        }

        public async ValueTask<Response> Invoke()
        {
            try
            {
                await _target!.DoWork();
                return Response.FromResult<object?>(null);
            }
            catch (Exception exception)
            {
                return Response.FromException(exception);
            }
        }

        public int GetArgumentCount() => 0;

        public object? GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));

        public void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(nameof(index));

        public string GetMethodName() => nameof(IStatelessWorkerSingleActivationGrain.DoWork);

        public string GetInterfaceName() => typeof(IStatelessWorkerSingleActivationGrain).FullName!;

        public string GetActivityName() => $"{GetInterfaceName()}/{GetMethodName()}";

        public MethodInfo GetMethod() => Method;

        public Type GetInterfaceType() => typeof(IStatelessWorkerSingleActivationGrain);

        public void Dispose()
        {
        }
    }
}

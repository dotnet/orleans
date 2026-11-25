using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Concurrency;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.Management;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class ManagementDeactivationTests(ManagementDeactivationTests.Fixture fixture)
    : IClassFixture<ManagementDeactivationTests.Fixture>
{
    public class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 2;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder builder) =>
                builder.Services.AddSingleton<ManagementDeactivationTracker>();
        }
    }

    private IEnumerable<InProcessSiloHandle> Silos => fixture.HostedCluster.GetActiveSilos().Cast<InProcessSiloHandle>();
    private IManagementGrain Management => fixture.Client.GetGrain<IManagementGrain>(0);

    private ManagementDeactivationTracker.Lifecycle[] Lifecycles(GrainId grainId) => Silos
        .Select(silo => silo.ServiceProvider.GetRequiredService<ManagementDeactivationTracker>().Get(grainId)).ToArray();

    private IGrainContext[] Contexts(GrainId grainId) => Silos
        .Select(silo => silo.ServiceProvider.GetRequiredService<ActivationDirectory>().FindTarget(grainId))
        .OfType<IGrainContext>().ToArray();

    [Fact]
    public async Task InactiveGrainIsNeverActivated()
    {
        var grain = fixture.Client.GetGrain<IManagementDeactivationTestGrain>(Guid.NewGuid());
        var grainId = grain.GetGrainId();

        await Management.DeactivateGrain(grainId, TestContext.Current.CancellationToken);
        await Management.DeactivateGrain(grainId, TestContext.Current.CancellationToken);

        Assert.Empty(Contexts(grainId));
        Assert.Equal(0, Lifecycles(grainId).Sum(lifecycle => lifecycle.Activations));
        Assert.Equal(0, Lifecycles(grainId).Sum(lifecycle => lifecycle.Deactivations));
    }

    [Fact]
    public async Task DeactivatesOnlyTheRequestedGrainWithoutReactivatingIt()
    {
        var grain = fixture.Client.GetGrain<IManagementDeactivationTestGrain>(Guid.NewGuid());
        var other = fixture.Client.GetGrain<IManagementDeactivationTestGrain>(Guid.NewGuid());
        await grain.Ping();
        await other.Ping();
        var grainId = grain.GetGrainId();
        var context = Assert.Single(Contexts(grainId));
        var otherContext = Assert.Single(Contexts(other.GetGrainId()));

        await Management.DeactivateGrain(grainId, TestContext.Current.CancellationToken);
        await Management.DeactivateGrain(grainId, TestContext.Current.CancellationToken);

        Assert.True(context.Deactivated.IsCompletedSuccessfully);
        Assert.Empty(Contexts(grainId));
        Assert.Same(otherContext, Assert.Single(Contexts(other.GetGrainId())));
        Assert.Equal(1, Lifecycles(grainId).Sum(lifecycle => lifecycle.Activations));
        Assert.Equal(1, Lifecycles(grainId).Sum(lifecycle => lifecycle.Deactivations));
        var lifecycle = Assert.Single(Lifecycles(grainId), item => item.Deactivations == 1);
        Assert.Equal(DeactivationReasonCode.ApplicationRequested, lifecycle.DeactivationReason.ReasonCode);
        Assert.Equal("Application requested deactivation", lifecycle.DeactivationReason.Description);

        await grain.Ping();
        Assert.NotSame(context, Assert.Single(Contexts(grainId)));
        Assert.Equal(2, Lifecycles(grainId).Sum(lifecycle => lifecycle.Activations));
    }

    [Fact]
    public async Task WaitsForDeactivationCallbacksToFinish()
    {
        var grain = fixture.Client.GetGrain<IManagementDeactivationTestGrain>(Guid.NewGuid());
        await grain.Ping();
        var grainId = grain.GetGrainId();
        var lifecycle = Assert.Single(Lifecycles(grainId), item => item.Activations == 1);
        lifecycle.BlockDeactivation = true;
        var started = lifecycle.DeactivationStarted.Task;
        var operation = Management.DeactivateGrain(grainId, TestContext.Current.CancellationToken);
        try
        {
            await started.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            lifecycle.ReleaseDeactivation.TrySetResult();
        }

        await operation;
        Assert.Empty(Contexts(grainId));
        Assert.Equal(1, lifecycle.Deactivations);
    }

    [Fact]
    public async Task DeactivatesStatelessWorkersOnEverySilo()
    {
        var key = Guid.NewGuid();
        foreach (var silo in Silos)
        {
            await silo.ServiceProvider.GetRequiredService<IGrainFactory>()
                .GetGrain<IManagementDeactivationTestWorker>(key).Ping();
        }
        var worker = fixture.Client.GetGrain<IManagementDeactivationTestWorker>(key);
        var grainId = worker.GetGrainId();
        Assert.Equal(2, Contexts(grainId).Length);
        Assert.Equal(2, Lifecycles(grainId).Sum(lifecycle => lifecycle.Activations));

        await Management.DeactivateGrain(grainId, TestContext.Current.CancellationToken);
        await Management.DeactivateGrain(grainId, TestContext.Current.CancellationToken);

        Assert.Empty(Contexts(grainId));
        Assert.Equal(2, Lifecycles(grainId).Sum(lifecycle => lifecycle.Activations));
        Assert.Equal(2, Lifecycles(grainId).Sum(lifecycle => lifecycle.Deactivations));
        Assert.All(Lifecycles(grainId), lifecycle =>
        {
            Assert.Equal(DeactivationReasonCode.ApplicationRequested, lifecycle.DeactivationReason.ReasonCode);
            Assert.Equal("Application requested deactivation", lifecycle.DeactivationReason.Description);
        });
    }
}

public interface IManagementDeactivationTestGrain : IGrainWithGuidKey
{
    Task Ping();
}

public interface IManagementDeactivationTestWorker : IGrainWithGuidKey
{
    Task Ping();
}

public class ManagementDeactivationTestGrain(ManagementDeactivationTracker tracker) : Grain, IManagementDeactivationTestGrain
{
    public Task Ping() => Task.CompletedTask;
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref tracker.Get(this.GetGrainId()).Activations);
        return Task.CompletedTask;
    }
    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken) =>
        tracker.DeactivateAsync(this.GetGrainId(), reason, cancellationToken);
}

[StatelessWorker(1)]
public class ManagementDeactivationTestWorker(ManagementDeactivationTracker tracker) : Grain, IManagementDeactivationTestWorker
{
    public Task Ping() => Task.CompletedTask;
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref tracker.Get(this.GetGrainId()).Activations);
        return Task.CompletedTask;
    }
    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken) =>
        tracker.DeactivateAsync(this.GetGrainId(), reason, cancellationToken);
}

public class ManagementDeactivationTracker
{
    private readonly ConcurrentDictionary<GrainId, Lifecycle> lifecycles = new();
    public Lifecycle Get(GrainId grainId) => lifecycles.GetOrAdd(grainId, static _ => new());

    public async Task DeactivateAsync(GrainId grainId, DeactivationReason reason, CancellationToken cancellationToken)
    {
        var lifecycle = Get(grainId);
        lifecycle.DeactivationReason = reason;
        lifecycle.DeactivationStarted.TrySetResult();
        if (lifecycle.BlockDeactivation)
        {
            await lifecycle.ReleaseDeactivation.Task.WaitAsync(cancellationToken);
        }
        Interlocked.Increment(ref lifecycle.Deactivations);
    }

    public sealed class Lifecycle
    {
        public int Activations;
        public int Deactivations;
        public DeactivationReason DeactivationReason;
        public volatile bool BlockDeactivation;
        public TaskCompletionSource DeactivationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDeactivation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

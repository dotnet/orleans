using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

/// <summary>
/// Verifies that <see cref="IActivationFilter"/> runs around grain activation and deactivation.
/// </summary>
[TestSuite("BVT"), TestProvider("None"), TestCategory("BVT")]
public sealed class ActivationFilterTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Filter_RunsAroundActivation_InOrder()
    {
        var key = Guid.NewGuid();
        var state = ActivationFilterTestState.Create(key);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddActivationFilter(async context =>
        {
            ActivationFilterTestState.For(context.GrainId).Events.Add("Filter-Before");
            await context.Invoke();
            ActivationFilterTestState.For(context.GrainId).Events.Add("Filter-After");
        })));

        var grain = GetGrainFactory(cluster).GetGrain<IActivationFilterGrain>(key, typeof(ActivationFilterGrain).FullName);
        await grain.Ping();

        Assert.Equal(["Filter-Before", "OnActivateAsync", "Filter-After", "Ping"], state.Events);
    }

    [Fact]
    public async Task Filter_RunsOnDeactivation_AndSeesReason()
    {
        var key = Guid.NewGuid();
        var state = ActivationFilterTestState.Create(key);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddActivationFilter(async context =>
        {
            var events = ActivationFilterTestState.For(context.GrainId).Events;
            events.Add(context.Stage == ActivationStage.Activating ? "Filter-Activate" : $"Filter-Deactivate:{context.DeactivationReason.ReasonCode}");
            await context.Invoke();
        })));

        var grain = GetGrainFactory(cluster).GetGrain<IActivationFilterGrain>(key, typeof(ActivationFilterGrain).FullName);
        await grain.Ping();
        await grain.Deactivate();
        await WaitUntilAsync(() => FindActivation(cluster.Silos[0], grain.GetGrainId()) is null);

        Assert.Equal(
            ["Filter-Activate", "OnActivateAsync", "Ping", $"Filter-Deactivate:{DeactivationReasonCode.ApplicationRequested}", "OnDeactivateAsync"],
            state.Events);
    }

    [Fact]
    public async Task TwoFilters_RunInRegistrationOrder()
    {
        var key = Guid.NewGuid();
        var state = ActivationFilterTestState.Create(key);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
        {
            silo.AddActivationFilter(async context =>
            {
                ActivationFilterTestState.For(context.GrainId).Events.Add("Filter1-Before");
                await context.Invoke();
                ActivationFilterTestState.For(context.GrainId).Events.Add("Filter1-After");
            });
            silo.AddActivationFilter(async context =>
            {
                ActivationFilterTestState.For(context.GrainId).Events.Add("Filter2-Before");
                await context.Invoke();
                ActivationFilterTestState.For(context.GrainId).Events.Add("Filter2-After");
            });
        }));

        var grain = GetGrainFactory(cluster).GetGrain<IActivationFilterGrain>(key, typeof(ActivationFilterGrain).FullName);
        await grain.Ping();

        Assert.Equal(
            ["Filter1-Before", "Filter2-Before", "OnActivateAsync", "Filter2-After", "Filter1-After", "Ping"],
            state.Events);
    }

    [Fact]
    public async Task FilterThrowingOnActivation_FailsActivation()
    {
        var key = Guid.NewGuid();
        var state = ActivationFilterTestState.Create(key);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddActivationFilter(context =>
        {
            ActivationFilterTestState.For(context.GrainId).Events.Add("Filter-Before");
            throw new InvalidOperationException("Activation filter failed.");
        })));

        var grain = GetGrainFactory(cluster).GetGrain<IActivationFilterGrain>(key, typeof(ActivationFilterGrain).FullName);
        await Assert.ThrowsAnyAsync<Exception>(() => grain.Ping().AsTask().WaitAsync(Timeout, Cancellation));

        // The filter ran, but the grain's activation hook never did, and the activation never became valid.
        Assert.DoesNotContain("OnActivateAsync", state.Events);
        await WaitUntilAsync(() => FindActivation(cluster.Silos[0], grain.GetGrainId()) is null);
    }

    [Fact]
    public async Task FilterThrowingOnDeactivation_DoesNotStopDeactivation()
    {
        var key = Guid.NewGuid();
        var state = ActivationFilterTestState.Create(key);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddActivationFilter(context =>
        {
            if (context.Stage == ActivationStage.Deactivating)
            {
                ActivationFilterTestState.For(context.GrainId).Events.Add("Filter-Deactivate-Throw");
                throw new InvalidOperationException("Deactivation filter failed.");
            }

            return context.Invoke();
        })));

        var grain = GetGrainFactory(cluster).GetGrain<IActivationFilterGrain>(key, typeof(ActivationFilterGrain).FullName);
        await grain.Ping();
        await grain.Deactivate();

        // Deactivation completes even though the filter threw; the grain's deactivation hook never ran.
        await WaitUntilAsync(() => FindActivation(cluster.Silos[0], grain.GetGrainId()) is null);
        Assert.Contains("Filter-Deactivate-Throw", state.Events);
        Assert.DoesNotContain("OnDeactivateAsync", state.Events);
    }

    [Fact]
    public async Task GrainImplementingFilter_IsInvokedAsStage()
    {
        var key = Guid.NewGuid();
        var state = ActivationFilterTestState.Create(key);
        await using var cluster = await DeployAsync();

        var grain = GetGrainFactory(cluster).GetGrain<IActivationFilterGrain>(key, typeof(SelfFilteringGrain).FullName);
        await grain.Ping();

        Assert.Equal(["GrainFilter-Before", "OnActivateAsync", "GrainFilter-After", "Ping"], state.Events);
    }

    private static IGrainFactory GetGrainFactory(InProcessTestCluster cluster)
        => cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();

    private static async Task<InProcessTestCluster> DeployAsync(Action<InProcessTestClusterBuilder>? configure = null)
    {
        var builder = new InProcessTestClusterBuilder(1);
        configure?.Invoke(builder);
        var cluster = builder.Build();
        await cluster.DeployAsync(Cancellation);
        await cluster.WaitForLivenessToStabilizeAsync().WaitAsync(Cancellation);
        await cluster.WaitForClusterManifestToStabilizeAsync().WaitAsync(Cancellation);
        return cluster;
    }

    private static IGrainContext? FindActivation(InProcessSiloHandle silo, GrainId grainId)
        => silo.ServiceProvider.GetRequiredService<ActivationDirectory>().FindTarget(grainId);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeLimit = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(timeLimit ?? Timeout);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}

/// <summary>
/// Per-grain-key event log shared between the test, the grain, and the filters, which all run in-process.
/// </summary>
public sealed class ActivationFilterTestState
{
    private static readonly ConcurrentDictionary<Guid, ActivationFilterTestState> States = new();

    public List<string> Events { get; } = [];

    public static ActivationFilterTestState Create(Guid key)
    {
        var state = new ActivationFilterTestState();
        States[key] = state;
        return state;
    }

    public static ActivationFilterTestState For(GrainId grainId) => States[grainId.GetGuidKey()];
}

public interface IActivationFilterGrain : IGrainWithGuidKey
{
    ValueTask Ping();

    ValueTask Deactivate();
}

public class ActivationFilterGrain : Grain, IActivationFilterGrain
{
    public ValueTask Ping()
    {
        ActivationFilterTestState.For(this.GrainContext.GrainId).Events.Add("Ping");
        return ValueTask.CompletedTask;
    }

    public ValueTask Deactivate()
    {
        this.DeactivateOnIdle();
        return ValueTask.CompletedTask;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        ActivationFilterTestState.For(this.GrainContext.GrainId).Events.Add("OnActivateAsync");
        return Task.CompletedTask;
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        ActivationFilterTestState.For(this.GrainContext.GrainId).Events.Add("OnDeactivateAsync");
        return Task.CompletedTask;
    }
}

/// <summary>
/// A grain which implements <see cref="IActivationFilter"/> itself, so it runs as a stage in its own pipeline.
/// </summary>
public sealed class SelfFilteringGrain : ActivationFilterGrain, IActivationFilter
{
    public async Task Invoke(IActivationContext context)
    {
        ActivationFilterTestState.For(context.GrainId).Events.Add("GrainFilter-Before");
        await context.Invoke();
        ActivationFilterTestState.For(context.GrainId).Events.Add("GrainFilter-After");
    }
}

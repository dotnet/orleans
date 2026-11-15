using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

[TestSuite("BVT"), TestProvider("None"), TestCategory("BVT")]
[Collection(StatelessWorkerActivationTimerCollection.Name)]
public sealed class StatelessWorkerActivationCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationCombinesCallerCancellationWithItsConfiguredTimeout(bool externalFirst)
    {
        await using var fixture = new StatelessWorkerActivationTimerTests.Fixture
        {
            ActivationTimeout = externalFirst ? TimeSpan.FromHours(1) : TimeSpan.FromSeconds(1)
        };
        await fixture.InitializeAsync();
        var token = TestContext.Current.CancellationToken;
        var services = fixture.HostedCluster.Silos[0].ServiceProvider;
        var registry = services.GetRequiredService<LifecycleCallbackRegistry>();
        var reference = fixture.GrainFactory.GetGrain<ILifecycleCallbackGrain>(
            $"{LifecycleCallback.OnActivateAsync}/{Guid.NewGuid():N}");
        var id = reference.GetGrainId();
        registry.Track(id);
        using var external = new CancellationTokenSource(TimeSpan.FromHours(1));
        // Exercise the caller-token boundary which the stateless wrapper delegates to.
        await using var activation = Assert.IsType<ActivationData>(services.GetRequiredService<GrainContextActivator>().CreateInstance(
            new GrainAddress { GrainId = id, ActivationId = ActivationId.NewId(), SiloAddress = fixture.HostedCluster.Silos[0].SiloAddress }));
        activation.Activate(null, external.Token);
        var probe = await registry.Get(id).Probes.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token);
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            Assert.False(external.IsCancellationRequested);
            if (externalFirst)
            {
                external.Cancel();
            }

            await probe.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            Assert.Equal(externalFirst, external.IsCancellationRequested);
            Assert.False(probe.ScopeDisposed);
            Assert.False(activation.Deactivated.IsCompleted);
        }
        finally
        {
            probe.Release.TrySetResult();
        }

        await activation.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), token);
        Assert.True(probe.ScopeDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrapperShutdownCancelsActivationBeforeItsOneHourTimeout(bool dispose)
    {
        await using var fixture = new StatelessWorkerActivationTimerTests.Fixture
        {
            ActivationTimeout = TimeSpan.FromHours(1)
        };
        await fixture.InitializeAsync();
        var token = TestContext.Current.CancellationToken;
        var registry = fixture.HostedCluster.Silos[0].ServiceProvider.GetRequiredService<LifecycleCallbackRegistry>();
        var grain = fixture.GrainFactory.GetGrain<IStatelessActivationTimeoutGrain>(
            $"{LifecycleCallback.OnActivateAsync}/{Guid.NewGuid():N}");
        var id = grain.GetGrainId();
        registry.Track(id);
        var call = grain.Ping();
        var probe = await registry.Get(id).Probes.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token);
        Task shutdown;
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            Assert.True(fixture.HostedCluster.TryGetGrainContext(id, out var context));
            var wrapper = Assert.IsType<StatelessWorkerGrainContext>(context);
            if (dispose)
            {
                shutdown = wrapper.DisposeAsync().AsTask();
            }
            else
            {
                shutdown = wrapper.Deactivated;
                wrapper.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Activation cancellation test."), CancellationToken.None);
            }

            await probe.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            Assert.False(shutdown.IsCompleted);
            Assert.False(probe.ScopeDisposed);
            Assert.False(probe.Context.Deactivated.IsCompleted);
        }
        finally
        {
            probe.Release.TrySetResult();
        }

        await probe.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), token);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(30), token);
        Assert.True(probe.ScopeDisposed);
        await call.WaitAsync(TimeSpan.FromSeconds(30), token);
        await grain.Ping().WaitAsync(TimeSpan.FromSeconds(30), token);
        Assert.Equal(2, registry.Get(id).ActivationCount);
    }
}

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class GrainRuntimeResolutionTests
{
    [Fact, TestCategory("BVT")]
    public void Constructor_DoesNotResolveRuntime()
    {
        var grainContext = new TestGrainContext();

        _ = new TestGrain(grainContext);

        Assert.Equal(0, grainContext.GetComponentCallCount);
        Assert.Equal(0, grainContext.ActivationServicesAccessCount);
    }

    [Fact, TestCategory("BVT")]
    public void Runtime_ContextComponent_ReturnsRuntime()
    {
        var grainRuntime = Substitute.For<IGrainRuntime>();
        var grainContext = new TestGrainContext();
        grainContext.SetComponent(grainRuntime);
        var grain = new TestGrain(grainContext);

        var first = grain.Runtime;
        var second = grain.Runtime;

        Assert.Same(grainRuntime, first);
        Assert.Same(first, second);
        Assert.Equal(2, grainContext.GetComponentCallCount);
        Assert.Equal(0, grainContext.ActivationServicesAccessCount);
    }

    [Fact, TestCategory("BVT")]
    public async Task Runtime_ConcurrentAccess_UsesContextRuntime()
    {
        var grainRuntime = Substitute.For<IGrainRuntime>();
        var grainContext = new TestGrainContext();
        grainContext.SetComponent(grainRuntime);
        var grain = new TestGrain(grainContext);

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => grain.Runtime)));

        Assert.All(results, result => Assert.Same(grainRuntime, result));
        Assert.Equal(results.Length, grainContext.GetComponentCallCount);
    }

    [Fact, TestCategory("BVT")]
    public void Runtime_ExplicitRuntime_RegistersContextComponent()
    {
        var grainContext = new TestGrainContext();
        var grainRuntime = Substitute.For<IGrainRuntime>();
        var grain = new TestGrain(grainContext, grainRuntime);

        Assert.Same(grainRuntime, ((IGrainContext)grainContext).GrainRuntime);
        Assert.Same(grainRuntime, grain.Runtime);
        Assert.Equal(1, grainContext.SetComponentCallCount);
        Assert.Equal(0, grainContext.ActivationServicesAccessCount);
    }

    [Fact, TestCategory("BVT")]
    public void Runtime_ExplicitRuntime_WorksWithMockContext()
    {
        var grainContext = Substitute.For<IGrainContext>();
        var grainRuntime = Substitute.For<IGrainRuntime>();
        var grain = new TestGrain(grainContext, grainRuntime);

        Assert.Same(grainRuntime, grainContext.GrainRuntime);
        Assert.Same(grainRuntime, grain.Runtime);
    }

    [Fact, TestCategory("BVT")]
    public void Runtime_ContextComponent_TakesPrecedenceOverActivationServices()
    {
        var componentRuntime = Substitute.For<IGrainRuntime>();
        var serviceRuntime = Substitute.For<IGrainRuntime>();
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(serviceRuntime)
            .BuildServiceProvider();
        var grainContext = new TestGrainContext(serviceProvider);
        grainContext.SetComponent(componentRuntime);
        var grain = new TestGrain(grainContext);

        Assert.Same(componentRuntime, grain.Runtime);
        Assert.Equal(0, grainContext.ActivationServicesAccessCount);
    }

    [Fact, TestCategory("BVT")]
    public void Runtime_ContextComponentUnavailable_UsesActivationServices()
    {
        var grainRuntime = Substitute.For<IGrainRuntime>();
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(grainRuntime)
            .BuildServiceProvider();
        var grainContext = new TestGrainContext(serviceProvider);
        var grain = new TestGrain(grainContext);

        Assert.Same(grainRuntime, grain.Runtime);
        Assert.Equal(1, grainContext.GetComponentCallCount);
        Assert.Equal(1, grainContext.ActivationServicesAccessCount);
    }

    [Fact, TestCategory("BVT")]
    public void Runtime_Unavailable_ThrowsDeterministicException()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var grain = new TestGrain(new TestGrainContext(serviceProvider));

        var exception = Assert.Throws<InvalidOperationException>(() => grain.Runtime);

        Assert.Equal("Grain was created outside of the Orleans creation process and no runtime was specified.", exception.Message);
    }

    [Fact, TestCategory("BVT")]
    public void Runtime_NullActivationServices_ThrowsDeterministicException()
    {
        var grain = new TestGrain(new TestGrainContext());

        var exception = Assert.Throws<InvalidOperationException>(() => grain.Runtime);

        Assert.Equal("Grain was created outside of the Orleans creation process and no runtime was specified.", exception.Message);
    }

    [Fact, TestCategory("BVT")]
    public void DirectConstruction_WithoutRuntime_PreservesOptionalRuntimeBehavior()
    {
        var grain = new TestGrain(null!);

        Assert.Null(grain.ServiceProvider);
        Assert.Empty(grain.RuntimeIdentity);
    }

    [Fact, TestCategory("BVT")]
    public async Task Runtime_RemainsAvailableAcrossLifecycleCallbacks()
    {
        var grainRuntime = Substitute.For<IGrainRuntime>();
        var grainContext = new TestGrainContext();
        grainContext.SetComponent(grainRuntime);
        var grain = new LifecycleGrain(grainContext);

        await grain.OnActivateAsync(CancellationToken.None);
        await grain.OnDeactivateAsync(new(DeactivationReasonCode.ApplicationRequested, "test"), CancellationToken.None);

        Assert.Same(grainRuntime, grain.ActivationRuntime);
        Assert.Same(grainRuntime, grain.DeactivationRuntime);
    }

    [Fact, TestCategory("BVT")]
    public void Grain_DoesNotRetainRuntimeField()
    {
        var runtimeFields = typeof(Grain)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(IGrainRuntime));

        Assert.Empty(runtimeFields);
    }

    private sealed class TestGrain(IGrainContext grainContext, IGrainRuntime? grainRuntime = null)
        : Grain(grainContext, grainRuntime);

    private sealed class LifecycleGrain(IGrainContext grainContext) : Grain(grainContext)
    {
        public IGrainRuntime? ActivationRuntime { get; private set; }

        public IGrainRuntime? DeactivationRuntime { get; private set; }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            ActivationRuntime = Runtime;
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            DeactivationRuntime = Runtime;
            return Task.CompletedTask;
        }
    }

    private sealed class TestGrainContext(IServiceProvider? activationServices = null) : IGrainContext
    {
        private readonly Dictionary<Type, object> _components = [];

        public int ActivationServicesAccessCount;
        public int GetComponentCallCount;
        public int SetComponentCallCount;

        public GrainReference GrainReference => throw new NotImplementedException();
        public GrainId GrainId => default;
        public object? GrainInstance => null;
        public ActivationId ActivationId => default;
        public GrainAddress Address => throw new NotImplementedException();
        public IServiceProvider ActivationServices
        {
            get
            {
                Interlocked.Increment(ref ActivationServicesAccessCount);
                return activationServices!;
            }
        }

        public IGrainLifecycle ObservableLifecycle => throw new NotImplementedException();
        public IWorkItemScheduler Scheduler => throw new NotImplementedException();
        public Task Deactivated => Task.CompletedTask;
        public PlacementStrategy PlacementStrategy => throw new NotImplementedException();

        public object? GetComponent(Type componentType)
        {
            Interlocked.Increment(ref GetComponentCallCount);
            return _components.TryGetValue(componentType, out var component) ? component : null;
        }

        public object? GetTarget() => null;

        public void SetComponent<TComponent>(TComponent? value) where TComponent : class
        {
            Interlocked.Increment(ref SetComponentCallCount);
            if (value is null)
            {
                _components.Remove(typeof(TComponent));
            }
            else
            {
                _components[typeof(TComponent)] = value;
            }
        }

        public void ReceiveMessage(object message) => throw new NotImplementedException();
        public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public void Rehydrate(IRehydrationContext context) => throw new NotImplementedException();
        public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public bool Equals(IGrainContext? other) => ReferenceEquals(this, other);
    }
}

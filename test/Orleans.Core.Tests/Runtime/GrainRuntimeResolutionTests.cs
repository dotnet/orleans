using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans;
using Orleans.Runtime;
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
        var grainContext = Substitute.For<IGrainContext>();

        _ = new TestGrain(grainContext);

        grainContext.DidNotReceive().GetComponent(typeof(IGrainRuntime));
        _ = grainContext.DidNotReceive().ActivationServices;
    }

    [Fact, TestCategory("BVT")]
    public void Runtime_FirstAccess_UsesContextComponentAndCachesResult()
    {
        var grainContext = Substitute.For<IGrainContext>();
        var grainRuntime = Substitute.For<IGrainRuntime>();
        grainContext.GetComponent(typeof(IGrainRuntime)).Returns(grainRuntime);
        var grain = new TestGrain(grainContext);

        var first = grain.Runtime;
        var second = grain.Runtime;

        Assert.Same(grainRuntime, first);
        Assert.Same(first, second);
        grainContext.Received(1).GetComponent(typeof(IGrainRuntime));
        _ = grainContext.DidNotReceive().ActivationServices;
    }

    [Fact, TestCategory("BVT")]
    public void Runtime_ExplicitRuntime_UsesProvidedInstance()
    {
        var grainContext = Substitute.For<IGrainContext>();
        var grainRuntime = Substitute.For<IGrainRuntime>();
        var grain = new TestGrain(grainContext, grainRuntime);

        Assert.Same(grainRuntime, grain.Runtime);
        grainContext.DidNotReceive().GetComponent(typeof(IGrainRuntime));
        _ = grainContext.DidNotReceive().ActivationServices;
    }

    [Fact, TestCategory("BVT")]
    public void Runtime_ContextComponentUnavailable_UsesActivationServices()
    {
        var grainContext = Substitute.For<IGrainContext>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var grainRuntime = Substitute.For<IGrainRuntime>();
        grainContext.ActivationServices.Returns(serviceProvider);
        serviceProvider.GetService(typeof(IGrainRuntime)).Returns(grainRuntime);
        var grain = new TestGrain(grainContext);

        Assert.Same(grainRuntime, grain.Runtime);
        grainContext.Received(1).GetComponent(typeof(IGrainRuntime));
        serviceProvider.Received(1).GetService(typeof(IGrainRuntime));
    }

    private sealed class TestGrain(IGrainContext grainContext, IGrainRuntime? grainRuntime = null)
        : Grain(grainContext, grainRuntime);
}

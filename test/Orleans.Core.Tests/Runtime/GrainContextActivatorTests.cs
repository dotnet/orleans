using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using NSubstitute;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class GrainContextActivatorTests
{
    [Fact]
    public void CreateInstance_ConfiguresContextBeforeStartingActivation()
    {
        var events = new List<string>();
        var context = Substitute.For<IGrainContext>();
        var contextActivator = new TestGrainContextActivator(context, events);
        var activator = new GrainContextActivator(
            [new TestGrainContextActivatorProvider(contextActivator)],
            [new TestConfigureGrainContextProvider(events)],
            new GrainPropertiesResolver(Substitute.For<IClusterManifestProvider>()));
        var address = new GrainAddress { GrainId = GrainId.Create("test", "grain") };

        Assert.Same(context, activator.CreateInstance(address));
        Assert.Equal(["configure", "activate"], events);
    }

    [Fact]
    public void UndecodedRequest_InterleavesForReentrantGrain()
    {
        var component = new GrainCanInterleave();
        component.MayInterleavePredicates.Add(ReentrantPredicate.Instance);
        var message = new Message { BodyObject = new UndecodedRequestBody([], "alias") };

        Assert.True(component.MayInterleave(new object(), message));
    }

    [Fact]
    public void UndecodedRequest_SkipsBodyDependentInterleavePredicate()
    {
        var invoked = false;
        var component = new GrainCanInterleave();
        component.MayInterleavePredicates.Add(new MayInterleaveStaticPredicate(_ =>
        {
            invoked = true;
            return true;
        }));
        var message = new Message { BodyObject = new UndecodedRequestBody([], "alias") };

        Assert.False(component.MayInterleave(new object(), message));
        Assert.False(invoked);
    }

    private sealed class TestGrainContextActivatorProvider(IGrainContextActivator activator) : IGrainContextActivatorProvider
    {
        public bool TryGet(GrainType grainType, [NotNullWhen(true)] out IGrainContextActivator? result)
        {
            result = activator;
            return true;
        }
    }

    private sealed class TestConfigureGrainContextProvider(List<string> events) : IConfigureGrainContextProvider
    {
        public bool TryGetConfigurator(
            GrainType grainType,
            GrainProperties properties,
            [NotNullWhen(true)] out IConfigureGrainContext? configurator)
        {
            configurator = new TestConfigureGrainContext(events);
            return true;
        }
    }

    private sealed class TestConfigureGrainContext(List<string> events) : IConfigureGrainContext
    {
        public void Configure(IGrainContext context) => events.Add("configure");
    }

    private sealed class TestGrainContextActivator(IGrainContext context, List<string> events) : IGrainContextActivator
    {
        public IGrainContext CreateContext(GrainAddress address, IConfigureGrainContext[] configureActions)
        {
            foreach (var configure in configureActions)
            {
                configure.Configure(context);
            }

            events.Add("activate");
            return context;
        }
    }
}

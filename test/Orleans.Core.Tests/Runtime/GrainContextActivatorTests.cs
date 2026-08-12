using System.Collections.Generic;
using NSubstitute;
using Orleans.Metadata;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime;

public class GrainContextActivatorTests
{
    [Fact, TestCategory("BVT")]
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

    private sealed class TestGrainContextActivatorProvider(IGrainContextActivator activator) : IGrainContextActivatorProvider
    {
        public bool TryGet(GrainType grainType, out IGrainContextActivator result)
        {
            result = activator;
            return true;
        }
    }

    private sealed class TestConfigureGrainContextProvider(List<string> events) : IConfigureGrainContextProvider
    {
        public bool TryGetConfigurator(GrainType grainType, GrainProperties properties, out IConfigureGrainContext configurator)
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

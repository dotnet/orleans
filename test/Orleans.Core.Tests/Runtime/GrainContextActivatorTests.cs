using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using NSubstitute;
using Orleans.Metadata;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
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

    [Fact]
    public void CreatePreparedContext_CustomActivatorRemainsEager()
    {
        var events = new List<string>();
        var context = Substitute.For<IGrainContext>();
        var activator = CreateActivator(new TestGrainContextActivator(context, events), events);
        var address = new GrainAddress { GrainId = GrainId.Create("test", "grain") };

        var preparedContext = activator.CreatePreparedContext(address);
        using var startup = preparedContext.Start();
        preparedContext.Abort();

        Assert.Same(context, preparedContext.Context);
        Assert.Equal(["configure", "activate"], events);
    }

    [Fact]
    public void CreateInstance_PreparedContextStartsAndReleasesExactlyOnce()
    {
        var events = new List<string>();
        var context = Substitute.For<IGrainContext>();
        var activator = CreateActivator(new TestPreparedGrainContextActivator(context, events), events);
        var address = new GrainAddress { GrainId = GrainId.Create("test", "grain") };

        Assert.Same(context, activator.CreateInstance(address));
        Assert.Equal(["configure", "create", "start", "release"], events);
    }

    private static GrainContextActivator CreateActivator(
        IGrainContextActivator contextActivator,
        List<string> events) =>
        new(
            [new TestGrainContextActivatorProvider(contextActivator)],
            [new TestConfigureGrainContextProvider(events)],
            new GrainPropertiesResolver(Substitute.For<IClusterManifestProvider>()));

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

    private sealed class TestPreparedGrainContextActivator(
        IGrainContext context,
        List<string> events) : IPreparedGrainContextActivator
    {
        public IGrainContext CreateContext(GrainAddress address, IConfigureGrainContext[] configureActions)
        {
            var preparedContext = CreatePreparedContext(address, configureActions);
            using var startup = preparedContext.Start();
            return preparedContext.Context;
        }

        public PreparedGrainContext CreatePreparedContext(
            GrainAddress address,
            IConfigureGrainContext[] configureActions)
        {
            foreach (var configure in configureActions)
            {
                configure.Configure(context);
            }

            events.Add("create");
            return new(context, new TestGrainContextStartup(events));
        }
    }

    private sealed class TestGrainContextStartup(List<string> events) : IGrainContextStartup
    {
        public IDisposable Start()
        {
            events.Add("start");
            return new TestStartupLease(events);
        }

        public void Abort() => events.Add("abort");
    }

    private sealed class TestStartupLease(List<string> events) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                events.Add("release");
            }
        }
    }
}

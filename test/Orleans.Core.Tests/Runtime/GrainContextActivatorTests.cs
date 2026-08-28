using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
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
    public void PreparedContextCreate_CustomActivatorRemainsEager()
    {
        var events = new List<string>();
        var context = Substitute.For<IGrainContext>();
        var activator = new TestGrainContextActivator(context, events);
        var address = new GrainAddress { GrainId = GrainId.Create("test", "grain") };
        IConfigureGrainContext[] configureActions = [new TestConfigureGrainContext(events)];

        var preparedContext = PreparedGrainContext.Create(activator, address, configureActions);
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

    [Fact]
    public void PreparedContext_AbortDoesNotStart()
    {
        var events = new List<string>();
        var context = Substitute.For<IGrainContext>();
        var preparedContext = new PreparedGrainContext(context, new TestGrainContextStartup(events));

        preparedContext.Abort();

        Assert.Equal(["abort"], events);
    }

    [Fact]
    public void PreparedContext_StartFailureAbortsExactlyOnce()
    {
        var events = new List<string>();
        var context = Substitute.For<IGrainContext>();
        var expected = new InvalidOperationException("start-fault");
        var preparedContext = new PreparedGrainContext(
            context,
            new ThrowingGrainContextStartup(events, expected));

        var actual = Assert.Throws<InvalidOperationException>(preparedContext.Start);

        Assert.Same(expected, actual);
        Assert.Equal(["start", "abort"], events);
    }

    [Fact]
    public void PreparedContext_StartAndAbortFailureReportsBothExceptions()
    {
        var events = new List<string>();
        var context = Substitute.For<IGrainContext>();
        var startupException = new InvalidOperationException("start-fault");
        var abortException = new InvalidOperationException("abort-fault");
        var preparedContext = new PreparedGrainContext(
            context,
            new ThrowingStartAndAbortGrainContextStartup(
                events,
                startupException,
                abortException));

        var actual = Assert.Throws<AggregateException>(preparedContext.Start);

        Assert.Equal(
            "Grain context startup failed and aborting the startup also failed.",
            actual.Message.Split(" (")[0]);
        Assert.Equal([startupException, abortException], actual.InnerExceptions);
        Assert.Equal(["start", "abort"], events);
    }

    [Fact]
    public void CatalogStartPreparedContext_StartFailureRemovesRecordedTarget()
    {
        var events = new List<string>();
        var grainId = GrainId.Create("test", "grain");
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(grainId);
        context.Equals(Arg.Any<IGrainContext>())
            .Returns(call => ReferenceEquals(context, call.Arg<IGrainContext>()));
        var expected = new InvalidOperationException("start-fault");
        var preparedContext = new PreparedGrainContext(
            context,
            new ThrowingGrainContextStartup(events, expected));
        using var services = new ServiceCollection()
            .AddMetrics()
            .AddSingleton<OrleansInstruments>()
            .AddSingleton<CatalogInstruments>()
            .BuildServiceProvider();
        var activations = new ActivationDirectory(services.GetRequiredService<CatalogInstruments>());
        activations.RecordNewTarget(context);

        var actual = Assert.Throws<InvalidOperationException>(
            () => Catalog.StartPreparedContext(preparedContext, context, activations));

        Assert.Same(expected, actual);
        Assert.Null(activations.FindTarget(grainId));
        Assert.Equal(0, activations.Count);
        Assert.Equal(["start", "abort"], events);
    }

    [Fact]
    public void CatalogAbortPreparedContext_EagerCustomContextIsDeactivated()
    {
        var context = Substitute.For<IGrainContext>();
        var reason = new DeactivationReason(
            DeactivationReasonCode.ActivationFailed,
            "pre-start-failure");
        var preparedContext = new PreparedGrainContext(context, startup: null);

        Catalog.AbortPreparedContext(preparedContext, context, reason);

        context.Received(1).Deactivate(reason, CancellationToken.None);
    }

    [Fact]
    public void CatalogAbortPreparedContext_PreparedContextIsAborted()
    {
        var events = new List<string>();
        var context = Substitute.For<IGrainContext>();
        var reason = new DeactivationReason(
            DeactivationReasonCode.ActivationFailed,
            "pre-start-failure");
        var preparedContext = new PreparedGrainContext(context, new TestGrainContextStartup(events));

        Catalog.AbortPreparedContext(preparedContext, context, reason);

        Assert.Equal(["abort"], events);
        context.DidNotReceive().Deactivate(Arg.Any<DeactivationReason>(), Arg.Any<CancellationToken>());
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

    private sealed class ThrowingGrainContextStartup(
        List<string> events,
        Exception exception) : IGrainContextStartup
    {
        public IDisposable Start()
        {
            events.Add("start");
            throw exception;
        }

        public void Abort() => events.Add("abort");
    }

    private sealed class ThrowingStartAndAbortGrainContextStartup(
        List<string> events,
        Exception startupException,
        Exception abortException) : IGrainContextStartup
    {
        public IDisposable Start()
        {
            events.Add("start");
            throw startupException;
        }

        public void Abort()
        {
            events.Add("abort");
            throw abortException;
        }
    }
}

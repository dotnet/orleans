using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using TestGrainInterfaces;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.Runtime;

[TestArea("Runtime")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[Trait("Phase", "6")]
public sealed class OutsideRuntimeClientMetaclusterTests
{
    [Fact]
    public async Task SendRequest_LocalDestination_UsesLocalSendWithoutInterClusterTransport()
    {
        using var fixture = new Fixture();
        fixture.PrepareStoppedLocalMessageCenter();
        var completion = new RecordingCompletion();

        fixture.Runtime.SendRequest(
            fixture.CreateTarget(Fixture.HomeCluster),
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            completion,
            InvokeMethodOptions.OneWay);
        await fixture.Logs.LocalMessageCenterCalled.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, completion.CompletionCount);
        Assert.Same(Response.Completed, completion.Response);
        Assert.Equal(0, fixture.Transport.CallCount);
        Assert.Contains(fixture.Logs.Messages, message => message.Contains(
            "client message center is not running", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendRequest_RemoteDestination_UsesResolvedDestinationCluster()
    {
        using var fixture = new Fixture();
        var completion = new RecordingCompletion();

        fixture.Runtime.SendRequest(
            fixture.CreateTarget(Fixture.RemoteCluster),
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            completion,
            InvokeMethodOptions.None);
        var call = await fixture.Transport.Called.Task.WaitAsync(TestContext.Current.CancellationToken);
        await completion.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ClusterIdentity(Fixture.ServiceId, Fixture.RemoteCluster), call.Destination);
        Assert.Equal(Fixture.RemoteCluster, call.Target.ClusterId);
        Assert.Same(Response.Completed, completion.Response);
    }

    [Fact]
    public async Task SendRequest_AsyncResolution_CompletesBeforeTransportSend()
    {
        using var fixture = new Fixture();
        var topologyGate = fixture.Topology.BlockNextRead();
        var completion = new RecordingCompletion();

        fixture.Runtime.SendRequest(
            fixture.CreateTarget(Fixture.RemoteCluster),
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            completion,
            InvokeMethodOptions.None);

        Assert.False(fixture.Transport.Called.Task.IsCompleted);
        topologyGate.SetResult(fixture.Topology.Current);
        await fixture.Transport.Called.Task.WaitAsync(TestContext.Current.CancellationToken);
        await completion.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Transport.CallCount);
        Assert.Equal(1, completion.CompletionCount);
    }

    [Fact]
    public async Task SendRequest_ClusterBoundReference_UsesBoundDestinationWithoutRelocation()
    {
        using var fixture = new Fixture();
        var completion = new RecordingCompletion();
        var target = fixture.CreateTarget(Fixture.RemoteCluster);

        fixture.Runtime.SendRequest(
            target,
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            completion,
            InvokeMethodOptions.None);
        var call = await fixture.Transport.Called.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UniversalReferenceBinding.Cluster, call.Target.Binding);
        Assert.Equal(Fixture.RemoteCluster, call.Destination.ClusterId);
        Assert.Equal(0, fixture.Locator.CallCount);
    }

    [Fact]
    public void SendRequest_ResolvedDestinationServiceMismatch_IsRejected()
    {
        using var fixture = new Fixture();
        var target = fixture.CreateTarget(Fixture.RemoteCluster, serviceId: "other-service");

        var exception = Assert.Throws<InvalidOperationException>(
            () => fixture.Runtime.SendRequest(
                target,
                fixture.CreateRequest(TestContext.Current.CancellationToken),
                new RecordingCompletion(),
                InvokeMethodOptions.None));

        Assert.Contains("does not match the local service", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Transport.CallCount);
    }

    [Fact]
    public async Task SendRequest_RemoteTimeout_IsTranslatedToResponseTimeout()
    {
        using var fixture = new Fixture();
        fixture.Transport.Handler = call =>
            ValueTask.FromException<Response>(new OperationCanceledException(call.CancellationToken));
        var completion = new RecordingCompletion();

        fixture.Runtime.SendRequest(
            fixture.CreateTarget(Fixture.RemoteCluster),
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            completion,
            InvokeMethodOptions.None);
        await completion.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);

        var exception = Assert.IsType<TimeoutException>(completion.Response!.Exception);
        Assert.Contains("inter-cluster request timeout", exception.Message, StringComparison.Ordinal);
        Assert.IsType<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public async Task SendRequest_CallerCancellation_RemainsCancellationNotTimeout()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Transport.Handler = call =>
        {
            cancellation.Cancel();
            return ValueTask.FromException<Response>(new OperationCanceledException(cancellation.Token));
        };
        var completion = new RecordingCompletion();

        fixture.Runtime.SendRequest(
            fixture.CreateTarget(Fixture.RemoteCluster),
            fixture.CreateRequest(cancellation.Token),
            completion,
            InvokeMethodOptions.None);
        await completion.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);

        var exception = Assert.IsType<OperationCanceledException>(completion.Response!.Exception);
        Assert.IsNotType<TimeoutException>(exception);
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task SendRequest_OneWayRemote_CompletesAfterTransportAcceptance()
    {
        using var fixture = new Fixture();
        var acceptance = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.Handler = _ => new ValueTask<Response>(acceptance.Task);
        var completion = new RecordingCompletion();

        fixture.Runtime.SendRequest(
            fixture.CreateTarget(Fixture.RemoteCluster),
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            completion,
            InvokeMethodOptions.OneWay);
        await fixture.Transport.Called.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, completion.CompletionCount);
        acceptance.SetResult(Response.Completed);
        await completion.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, completion.CompletionCount);
        Assert.Same(Response.Completed, completion.Response);
    }

    [Fact]
    public async Task SendRequest_RemoteFailure_IsPropagatedAndLoggedOnce()
    {
        using var fixture = new Fixture();
        var expected = new InvalidOperationException("remote transport failed");
        fixture.Transport.Handler = _ => ValueTask.FromException<Response>(expected);
        var completion = new RecordingCompletion();

        fixture.Runtime.SendRequest(
            fixture.CreateTarget(Fixture.RemoteCluster),
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            completion,
            InvokeMethodOptions.None);
        await completion.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);
        fixture.Runtime.SendRequest(
            fixture.CreateTarget(Fixture.RemoteCluster),
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            context: null,
            InvokeMethodOptions.OneWay);
        await fixture.Logs.WarningWritten.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Same(expected, completion.Response!.Exception);
        Assert.Equal(2, fixture.Transport.CallCount);
        Assert.Single(fixture.Logs.Messages, message => message.Contains(
            "Failed to send one-way call to cluster remote", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendRequest_ExportsRequestContext()
    {
        using var fixture = new Fixture();
        RequestContext.Clear();
        RequestContext.Set("phase6-route", "west");
        try
        {
            var completion = new RecordingCompletion();
            fixture.Runtime.SendRequest(
                fixture.CreateVirtualTarget(),
                fixture.CreateRequest(TestContext.Current.CancellationToken),
                completion,
                InvokeMethodOptions.None);
            await fixture.Transport.Called.Task.WaitAsync(TestContext.Current.CancellationToken);

            Assert.Equal("west", fixture.Locator.LastContext!.RequestContext!["phase6-route"]);
            Assert.Equal(Fixture.RemoteCluster, fixture.Transport.LastCall!.Value.Destination.ClusterId);
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    [Fact]
    public void CreateObjectReference_BindsObserverToHomeCluster()
    {
        using var fixture = new Fixture();
        var observer = new Observer();
        var reference = fixture.Environment.GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer);
        try
        {
            var universal = reference.GetUniversalReference();

            Assert.Equal(UniversalReferenceBinding.Cluster, universal.Binding);
            Assert.Equal(Fixture.ServiceId, universal.ServiceId);
            Assert.Equal(Fixture.HomeCluster, universal.ClusterId);
            Assert.True(universal.GrainId.IsClient());
        }
        finally
        {
            fixture.Environment.GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
        }
    }

    [Fact]
    public void CreateObjectReference_CastAndSerializationPreserveHomeCluster()
    {
        using var fixture = new Fixture();
        var observer = new Observer();
        var reference = fixture.Environment.GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer);
        try
        {
            var cast = reference.AsReference<IClusterTestListener>();
            var roundTrip = Assert.IsAssignableFrom<GrainReference>(fixture.Environment.Serializer.Deserialize<GrainReference>(
                fixture.Environment.Serializer.SerializeToArray((GrainReference)cast)));

            Assert.Equal(reference.GetGrainId(), roundTrip.GrainId);
            Assert.Equal(Fixture.ServiceId, roundTrip.UniversalReference.ServiceId);
            Assert.Equal(UniversalReferenceBinding.Cluster, roundTrip.UniversalReference.Binding);
            Assert.Equal(Fixture.HomeCluster, roundTrip.UniversalReference.ClusterId);
            Assert.NotEqual(
                reference.GetUniversalReference().InterfaceType,
                roundTrip.UniversalReference.InterfaceType);
        }
        finally
        {
            fixture.Environment.GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
        }
    }

    [Fact]
    public async Task SendRequest_SystemTargetRetainsEncodedTargetSilo()
    {
        using var fixture = new Fixture();
        var expectedSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 32061), 61);
        var grainId = SystemTargetGrainId.Create(Constants.SiloControlType, expectedSilo, "phase6").GrainId;
        var interfaceType = fixture.Environment.Services
            .GetRequiredService<GrainInterfaceTypeResolver>()
            .GetGrainInterfaceType(typeof(ISiloControl));
        var target = fixture.CreateTarget(Fixture.RemoteCluster, grainId: grainId, interfaceType: interfaceType);

        fixture.Runtime.SendRequest(
            target,
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            new RecordingCompletion(),
            InvokeMethodOptions.None);
        var call = await fixture.Transport.Called.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(SystemTargetGrainId.TryParse(call.Target.GrainId, out var systemTargetId));
        Assert.Equal(expectedSilo, systemTargetId.GetSiloAddress());
        Assert.Equal(Fixture.RemoteCluster, call.Destination.ClusterId);
        Assert.Equal(interfaceType, call.Target.InterfaceType);
    }

    [Fact]
    public async Task SendRequest_ResolutionBudgetIsIndependentFromTransportBudget()
    {
        using var fixture = new Fixture();
        var completion = new RecordingCompletion();
        var resolution = new TaskCompletionSource<ClusterIdentity>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resolutionBudget = new CancellationTokenSource();
        var resolutionToken = resolutionBudget.Token;
        resolutionBudget.Cancel();
        var method = typeof(OutsideRuntimeClient).GetMethod(
            "ResolveAndSendRequest",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pending = Assert.IsAssignableFrom<Task>(method.Invoke(
            fixture.Runtime,
            [
                new ValueTask<ClusterIdentity>(resolution.Task),
                fixture.CreateTarget(Fixture.RemoteCluster),
                fixture.CreateRequest(TestContext.Current.CancellationToken),
                completion,
                InvokeMethodOptions.None,
                resolutionBudget
            ]));

        resolution.SetResult(new ClusterIdentity(Fixture.ServiceId, Fixture.RemoteCluster));
        var call = await fixture.Transport.Called.Task.WaitAsync(TestContext.Current.CancellationToken);
        await completion.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);
        await pending.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(resolutionToken.IsCancellationRequested);
        Assert.NotEqual(resolutionToken, call.CancellationToken);
        Assert.False(call.CancellationToken.IsCancellationRequested);
        Assert.Equal(1, completion.CompletionCount);
        Assert.Same(Response.Completed, completion.Response);
    }

    [Fact]
    public async Task SendRequest_RemoteTerminalOutcomes_CompleteOrLogExactlyOnce()
    {
        using var fixture = new Fixture();
        var expected = new InvalidOperationException("terminal remote failure");
        fixture.Transport.Handler = _ => ValueTask.FromException<Response>(expected);
        var completion = new RecordingCompletion();

        fixture.Runtime.SendRequest(
            fixture.CreateTarget(Fixture.RemoteCluster),
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            completion,
            InvokeMethodOptions.None);
        await completion.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);
        fixture.Runtime.SendRequest(
            fixture.CreateTarget(Fixture.RemoteCluster),
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            context: null,
            InvokeMethodOptions.OneWay);
        await fixture.Logs.WarningWritten.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, completion.CompletionCount);
        Assert.Same(expected, completion.Response!.Exception);
        Assert.Equal(2, fixture.Transport.CallCount);
        Assert.Single(
            fixture.Logs.Messages,
            message => message.Contains(
                "Failed to send one-way call to cluster remote",
                StringComparison.Ordinal));
    }

    private sealed class Fixture : IDisposable
    {
        public const string ServiceId = "phase6-service";
        public const string HomeCluster = "home";
        public const string RemoteCluster = "remote";
        private const string LocatorName = "phase6-locator";
        private static readonly GrainType LocatedGrainType = GrainType.Create("phase6.located");
        private static readonly GrainType TargetGrainType = GrainType.Create("phase6.target");
        private readonly ServiceProvider _locatorServices;

        public Fixture()
        {
            Transport = new RecordingTransport();
            Topology = new ControlledTopologyProvider(CreateTopology());
            Locator = new RecordingLocator();
            Logs = new RecordingLoggerProvider();

            var properties = new GrainProperties(
                ImmutableDictionary<string, string>.Empty
                    .WithComparers(StringComparer.Ordinal, StringComparer.Ordinal)
                    .Add(WellKnownGrainTypeProperties.ClusterLocator, LocatorName));
            var manifest = new GrainManifest(
                ImmutableDictionary<GrainType, GrainProperties>.Empty
                    .Add(LocatedGrainType, properties)
                    .Add(TargetGrainType, new GrainProperties(
                        ImmutableDictionary<string, string>.Empty
                            .WithComparers(StringComparer.Ordinal, StringComparer.Ordinal))),
                ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
            var manifestProvider = Substitute.For<IClusterManifestProvider>();
            manifestProvider.Current.Returns(new ClusterManifest(
                MajorMinorVersion.Zero,
                ImmutableDictionary<SiloAddress, GrainManifest>.Empty,
                [manifest]));
            manifestProvider.LocalGrainManifest.Returns(manifest);
            var propertiesResolver = new GrainPropertiesResolver(manifestProvider);
            var locatorServices = new ServiceCollection();
            locatorServices.AddKeyedSingleton<IClusterLocator>(LocatorName, Locator);
            _locatorServices = locatorServices.BuildServiceProvider();
            var referenceResolver = new ClusterReferenceResolver(
                Options.Create(new ClusterOptions { ServiceId = ServiceId, ClusterId = HomeCluster }),
                Options.Create(new MetaclusterOptions { Enabled = true }),
                new ClusterLocatorResolver(propertiesResolver, _locatorServices),
                propertiesResolver,
                Topology,
                TimeProvider.System);
            var bindingResolver = new UniversalReferenceBindingResolver(
                Options.Create(new ClusterOptions { ServiceId = ServiceId, ClusterId = HomeCluster }),
                Options.Create(new MetaclusterOptions { Enabled = true }),
                propertiesResolver);

            Environment = new SerializationTestEnvironment(builder =>
            {
                builder.Configure<ClusterOptions>(options =>
                {
                    options.ServiceId = ServiceId;
                    options.ClusterId = HomeCluster;
                });
                builder.UseMetacluster();
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IInterClusterTransport>(Transport);
                    services.AddSingleton<IMetaclusterTopologyProvider>(Topology);
                    services.AddSingleton(referenceResolver);
                    services.AddSingleton(bindingResolver);
                    services.AddLogging(logging => logging.AddProvider(Logs));
                });
            });
            Runtime = Environment.RuntimeClient;
        }

        public SerializationTestEnvironment Environment { get; }

        public OutsideRuntimeClient Runtime { get; }

        public RecordingTransport Transport { get; }

        public ControlledTopologyProvider Topology { get; }

        public RecordingLocator Locator { get; }

        public RecordingLoggerProvider Logs { get; }

        public GrainReference CreateTarget(
            string clusterId,
            string serviceId = ServiceId,
            GrainId grainId = default,
            GrainInterfaceType interfaceType = default)
        {
            if (grainId.IsDefault)
            {
                grainId = GrainId.Create(TargetGrainType, "key");
            }

            var universal = UniversalReference.CreateCluster(
                grainId,
                interfaceType,
                serviceId,
                clusterId);
            return (GrainReference)Environment.InternalGrainFactory.GetGrain(universal);
        }

        public GrainReference CreateVirtualTarget()
        {
            var universal = UniversalReference.CreateVirtual(
                GrainId.Create(LocatedGrainType, "key"),
                default,
                ServiceId);
            return (GrainReference)Environment.InternalGrainFactory.GetGrain(universal);
        }

        public IInvokable CreateRequest(CancellationToken cancellationToken = default)
        {
            var request = Substitute.For<IInvokable>();
            request.GetCancellationToken().Returns(cancellationToken);
            return request;
        }

        public void PrepareStoppedLocalMessageCenter()
        {
            var messageCenter = ActivatorUtilities.CreateInstance<ClientMessageCenter>(Environment.Services);
            typeof(OutsideRuntimeClient)
                .GetProperty(nameof(OutsideRuntimeClient.MessageCenter), BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Runtime, messageCenter);
            var local = Environment.Services.GetRequiredService<LocalClientDetails>();
            typeof(OutsideRuntimeClient)
                .GetProperty(nameof(OutsideRuntimeClient.CurrentActivationAddress), BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(Runtime, GrainAddress.NewActivationAddress(local.ClientAddress, local.ClientId.GrainId));
        }

        public void Dispose()
        {
            Environment.Dispose();
            _locatorServices.Dispose();
            Logs.Dispose();
        }

        private static MetaclusterTopology CreateTopology() =>
            new(
                ServiceId,
                6,
                ImmutableDictionary<string, MetaclusterCluster>.Empty
                    .WithComparers(StringComparer.Ordinal)
                    .Add(HomeCluster, new MetaclusterCluster(HomeCluster, MetaclusterClusterState.Active, []))
                    .Add(RemoteCluster, new MetaclusterCluster(RemoteCluster, MetaclusterClusterState.Active, [])));
    }

    private sealed class RecordingTransport : IInterClusterTransport
    {
        private int _callCount;

        public Func<TransportCall, ValueTask<Response>> Handler { get; set; } =
            static _ => new ValueTask<Response>(Response.Completed);

        public TaskCompletionSource<TransportCall> Called { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public TransportCall? LastCall { get; private set; }

        public ValueTask<Response> SendRequest(
            ClusterIdentity destination,
            UniversalReference target,
            IInvokable request,
            InvokeMethodOptions options,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            var call = new TransportCall(destination, target, request, options, cancellationToken);
            LastCall = call;
            Called.TrySetResult(call);
            return Handler(call);
        }
    }

    private readonly record struct TransportCall(
        ClusterIdentity Destination,
        UniversalReference Target,
        IInvokable Request,
        InvokeMethodOptions Options,
        CancellationToken CancellationToken);

    private sealed class RecordingCompletion : IResponseCompletionSource
    {
        private int _completionCount;

        public TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CompletionCount => Volatile.Read(ref _completionCount);

        public Response? Response { get; private set; }

        public void Complete()
        {
            Response = Response.Completed;
            Interlocked.Increment(ref _completionCount);
            Completed.TrySetResult();
        }

        public void Complete(Response value)
        {
            Response = value;
            Interlocked.Increment(ref _completionCount);
            Completed.TrySetResult();
        }
    }

    private sealed class ControlledTopologyProvider(MetaclusterTopology current) : IMetaclusterTopologyProvider
    {
        private TaskCompletionSource<MetaclusterTopology>? _nextRead;

        public MetaclusterTopology Current { get; } = current;

        public TaskCompletionSource<MetaclusterTopology> BlockNextRead()
        {
            var result = new TaskCompletionSource<MetaclusterTopology>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _nextRead = result;
            return result;
        }

        public ValueTask<MetaclusterTopology> GetTopology(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _nextRead, null) is { } next)
            {
                return new ValueTask<MetaclusterTopology>(next.Task.WaitAsync(cancellationToken));
            }

            return new ValueTask<MetaclusterTopology>(Current);
        }

        public async IAsyncEnumerable<MetaclusterTopology> Watch(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingLocator : IClusterLocator
    {
        public int CallCount { get; private set; }

        public ClusterLocationContext? LastContext { get; private set; }

        public ValueTask<ClusterLocation> Locate(
            GrainId grainId,
            ClusterLocationContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastContext = context;
            return new ValueTask<ClusterLocation>(
                new ClusterLocation(Fixture.RemoteCluster, 1, 6, false));
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public TaskCompletionSource WarningWritten { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LocalMessageCenterCalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) =>
            new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(RecordingLoggerProvider owner, string category)
            : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Warning)
                {
                    return;
                }

                var message = formatter(state, exception);
                owner.Messages.Enqueue(message);
                if (category.Contains(nameof(OutsideRuntimeClient), StringComparison.Ordinal)
                    && logLevel == LogLevel.Warning)
                {
                    owner.WarningWritten.TrySetResult();
                }

                if (category.Contains(nameof(ClientMessageCenter), StringComparison.Ordinal)
                    && message.Contains("client message center is not running", StringComparison.Ordinal))
                {
                    owner.LocalMessageCenterCalled.TrySetResult();
                }
            }
        }
    }

    private sealed class Observer : ISimpleGrainObserver
    {
        public void StateChanged(int a, int b)
        {
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.Serialization.Invocation;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Tester;

[TestArea("Runtime")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[Trait("Phase", "6")]
public sealed class InsideRuntimeClientMetaclusterTests(InsideRuntimeClientMetaclusterFixture fixture)
    : IClassFixture<InsideRuntimeClientMetaclusterFixture>
{
    [Fact]
    public async Task MetaclusterSend_LocalDestination_UsesLocalMessageCenter()
    {
        fixture.Reset();

        var grain = fixture.Runtime.ConcreteGrainFactory.GetGrain<IPhase6LocalRoutingGrain>("local-route");
        var result = await grain.Echo("payload");

        Assert.Equal("local:payload", result);
        Assert.Equal(0, fixture.Transport.CallCount);
    }

    [Fact]
    public async Task MetaclusterSend_RemoteDestination_UsesResolvedDestinationCluster()
    {
        fixture.Reset();
        var completion = new RecordingCompletion();

        fixture.Runtime.SendRequest(
            fixture.CreateTarget(InsideRuntimeClientMetaclusterFixture.RemoteCluster),
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            completion,
            InvokeMethodOptions.None);
        var call = await fixture.Transport.Called.Task.WaitAsync(TestContext.Current.CancellationToken);
        await completion.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            new ClusterIdentity(
                InsideRuntimeClientMetaclusterFixture.ServiceId,
                InsideRuntimeClientMetaclusterFixture.RemoteCluster),
            call.Destination);
        Assert.Equal(InsideRuntimeClientMetaclusterFixture.RemoteCluster, call.Target.ClusterId);
        Assert.Same(Response.Completed, completion.Response);
    }

    [Fact]
    public async Task MetaclusterSend_AsyncResolution_CompletesBeforeTransportSend()
    {
        fixture.Reset();
        var gate = fixture.Topology.BlockNextRead();
        var completion = new RecordingCompletion();

        fixture.Runtime.SendRequest(
            fixture.CreateTarget(InsideRuntimeClientMetaclusterFixture.RemoteCluster),
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            completion,
            InvokeMethodOptions.None);

        Assert.False(fixture.Transport.Called.Task.IsCompleted);
        gate.SetResult(fixture.Topology.Current);
        await fixture.Transport.Called.Task.WaitAsync(TestContext.Current.CancellationToken);
        await completion.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Transport.CallCount);
        Assert.Equal(1, completion.CompletionCount);
    }

    [Fact]
    public async Task MetaclusterSend_ClusterBoundReference_UsesBoundDestination()
    {
        fixture.Reset();
        var target = fixture.CreateTarget(InsideRuntimeClientMetaclusterFixture.RemoteCluster);

        fixture.Runtime.SendRequest(
            target,
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            new RecordingCompletion(),
            InvokeMethodOptions.None);
        var call = await fixture.Transport.Called.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UniversalReferenceBinding.Cluster, call.Target.Binding);
        Assert.Equal(InsideRuntimeClientMetaclusterFixture.RemoteCluster, call.Destination.ClusterId);
        Assert.Equal(0, fixture.Locator.CallCount);
    }

    [Fact]
    public async Task MetaclusterSend_RemoteTimeout_IsTranslatedToResponseTimeout()
    {
        fixture.Reset();
        fixture.Transport.Handler = call =>
            ValueTask.FromException<Response>(new OperationCanceledException(call.CancellationToken));
        var completion = new RecordingCompletion();

        fixture.Runtime.SendRequest(
            fixture.CreateTarget(InsideRuntimeClientMetaclusterFixture.RemoteCluster),
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            completion,
            InvokeMethodOptions.None);
        await completion.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);

        var exception = Assert.IsType<TimeoutException>(completion.Response!.Exception);
        Assert.Contains("inter-cluster request timeout", exception.Message, StringComparison.Ordinal);
        Assert.IsType<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public async Task MetaclusterSend_CallerCancellation_RemainsCancellationNotTimeout()
    {
        fixture.Reset();
        using var cancellation = new CancellationTokenSource();
        fixture.Transport.Handler = _ =>
        {
            cancellation.Cancel();
            return ValueTask.FromException<Response>(new OperationCanceledException(cancellation.Token));
        };
        var completion = new RecordingCompletion();

        fixture.Runtime.SendRequest(
            fixture.CreateTarget(InsideRuntimeClientMetaclusterFixture.RemoteCluster),
            fixture.CreateRequest(cancellation.Token),
            completion,
            InvokeMethodOptions.None);
        await completion.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.IsType<OperationCanceledException>(completion.Response!.Exception);
        Assert.IsNotType<TimeoutException>(completion.Response.Exception);
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task MetaclusterSend_OneWayRemote_CompletesAfterTransportAcceptance()
    {
        fixture.Reset();
        var acceptance = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.Handler = _ => new ValueTask<Response>(acceptance.Task);
        var completion = new RecordingCompletion();

        fixture.Runtime.SendRequest(
            fixture.CreateTarget(InsideRuntimeClientMetaclusterFixture.RemoteCluster),
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
    public async Task MetaclusterSend_ExportsRequestContext()
    {
        fixture.Reset();
        RequestContext.Clear();
        RequestContext.Set("phase6-route", "east");
        try
        {
            fixture.Runtime.SendRequest(
                fixture.CreateVirtualTarget(),
                fixture.CreateRequest(TestContext.Current.CancellationToken),
                new RecordingCompletion(),
                InvokeMethodOptions.None);
            await fixture.Transport.Called.Task.WaitAsync(TestContext.Current.CancellationToken);

            Assert.Equal("east", fixture.Locator.LastContext!.RequestContext!["phase6-route"]);
            Assert.Equal(
                InsideRuntimeClientMetaclusterFixture.RemoteCluster,
                fixture.Transport.LastCall!.Value.Destination.ClusterId);
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    [Fact]
    public void MetaclusterSend_SystemTargetSetsTargetSiloFromGrainId()
    {
        fixture.Reset();
        var expectedSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 32162), 62);
        var grainId = SystemTargetGrainId.Create(Constants.SiloControlType, expectedSilo, "phase6").GrainId;
        var interfaceType = fixture.SiloServices
            .GetRequiredService<GrainInterfaceTypeResolver>()
            .GetGrainInterfaceType(typeof(ISiloControl));
        var target = fixture.CreateTarget(
            InsideRuntimeClientMetaclusterFixture.HomeCluster,
            grainId: grainId,
            interfaceType: interfaceType);
        var completion = new RecordingCompletion();
        var messageCenterField = typeof(InsideRuntimeClient)
            .GetField("messageCenter", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalMessageCenter = messageCenterField.GetValue(fixture.Runtime);

        try
        {
            messageCenterField.SetValue(fixture.Runtime, null);
            Assert.Throws<NullReferenceException>(
                () => fixture.Runtime.SendRequest(
                    target,
                    fixture.CreateRequest(TestContext.Current.CancellationToken),
                    completion,
                    InvokeMethodOptions.None));
            var callbacks = GetCallbacks(fixture.Runtime);
            var callback = Assert.Single(callbacks.Values);

            Assert.Equal(expectedSilo, callback.Message.TargetSilo);
            Assert.Equal(grainId, callback.Message.TargetGrain);
            Assert.True(callback.Message.IsSystemMessage);
            callback.OnHostShutdown();
        }
        finally
        {
            messageCenterField.SetValue(fixture.Runtime, originalMessageCenter);
        }
    }

    [Fact]
    public async Task MetaclusterSend_WrongDestinationServiceOrCluster_IsRejected()
    {
        fixture.Reset();
        var wrongService = Assert.Throws<InvalidOperationException>(
            () => fixture.Runtime.SendRequest(
                fixture.CreateTarget(
                    InsideRuntimeClientMetaclusterFixture.RemoteCluster,
                    serviceId: "wrong-service"),
                fixture.CreateRequest(TestContext.Current.CancellationToken),
                new RecordingCompletion(),
                InvokeMethodOptions.None));
        fixture.Topology.Current = fixture.CreateTopology(remoteState: MetaclusterClusterState.Removed);
        var wrongClusterCompletion = new RecordingCompletion();

        fixture.Runtime.SendRequest(
            fixture.CreateTarget(InsideRuntimeClientMetaclusterFixture.RemoteCluster),
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            wrongClusterCompletion,
            InvokeMethodOptions.None);

        Assert.Contains("does not match the local service", wrongService.Message, StringComparison.Ordinal);
        await wrongClusterCompletion.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Contains(
            "unavailable cluster",
            Assert.IsType<InvalidOperationException>(wrongClusterCompletion.Response!.Exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(0, fixture.Transport.CallCount);
    }

    [Fact]
    public async Task ObserverReference_RemoteCallback_ReturnsThroughSourceClientDirectory()
    {
        fixture.Reset();
        var target = fixture.CreateObserverTarget(InsideRuntimeClientMetaclusterFixture.RemoteCluster);
        var completion = new RecordingCompletion();

        fixture.Runtime.SendRequest(
            target,
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            completion,
            InvokeMethodOptions.None);
        var call = await fixture.Transport.Called.Task.WaitAsync(TestContext.Current.CancellationToken);
        await completion.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(call.Target.GrainId.IsClient());
        Assert.Equal(UniversalReferenceBinding.Cluster, call.Target.Binding);
        Assert.Equal(InsideRuntimeClientMetaclusterFixture.RemoteCluster, call.Destination.ClusterId);
        Assert.Same(Response.Completed, completion.Response);
    }

    [Fact]
    public async Task ObserverReference_CallbackAfterDestinationTopologyChange_StillRoutesHome()
    {
        fixture.Reset();
        var target = fixture.CreateObserverTarget(InsideRuntimeClientMetaclusterFixture.RemoteCluster);
        fixture.Topology.Current = fixture.CreateTopology(epoch: 7);

        fixture.Runtime.SendRequest(
            target,
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            new RecordingCompletion(),
            InvokeMethodOptions.None);
        var call = await fixture.Transport.Called.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, fixture.Topology.Current.Epoch);
        Assert.Equal(InsideRuntimeClientMetaclusterFixture.RemoteCluster, call.Target.ClusterId);
        Assert.Equal(InsideRuntimeClientMetaclusterFixture.RemoteCluster, call.Destination.ClusterId);
    }

    [Fact]
    public void ObserverReference_WrongSourceClusterBinding_IsRejected()
    {
        fixture.Reset();
        var target = fixture.CreateObserverTarget(
            InsideRuntimeClientMetaclusterFixture.RemoteCluster,
            serviceId: "other-service");

        var exception = Assert.Throws<InvalidOperationException>(
            () => fixture.Runtime.SendRequest(
                target,
                fixture.CreateRequest(TestContext.Current.CancellationToken),
                new RecordingCompletion(),
                InvokeMethodOptions.None));

        Assert.Contains("does not match the local service", exception.Message, StringComparison.Ordinal);
        Assert.True(target.GrainId.IsClient());
        Assert.Equal(0, fixture.Transport.CallCount);
    }

    [Fact]
    public async Task ObserverReference_CancellationAndRemoteError_ReturnToOriginalCaller()
    {
        fixture.Reset();
        using var cancellation = new CancellationTokenSource();
        fixture.Transport.Handler = _ =>
        {
            cancellation.Cancel();
            return ValueTask.FromException<Response>(new OperationCanceledException(cancellation.Token));
        };
        var cancellationCompletion = new RecordingCompletion();
        var target = fixture.CreateObserverTarget(InsideRuntimeClientMetaclusterFixture.RemoteCluster);

        fixture.Runtime.SendRequest(
            target,
            fixture.CreateRequest(cancellation.Token),
            cancellationCompletion,
            InvokeMethodOptions.None);
        await cancellationCompletion.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);

        var remoteError = new ApplicationException("observer callback failed");
        fixture.Transport.Handler = _ => ValueTask.FromException<Response>(remoteError);
        var errorCompletion = new RecordingCompletion();
        fixture.Runtime.SendRequest(
            target,
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            errorCompletion,
            InvokeMethodOptions.None);
        await errorCompletion.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.IsType<OperationCanceledException>(cancellationCompletion.Response!.Exception);
        Assert.Same(remoteError, errorCompletion.Response!.Exception);
        Assert.Equal(2, fixture.Transport.CallCount);
    }

    [Fact]
    public async Task MetaclusterSend_ResolutionBudgetIsIndependentFromTransportBudget()
    {
        fixture.Reset();
        var completion = new RecordingCompletion();
        var resolution = new TaskCompletionSource<ClusterIdentity>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resolutionBudget = new CancellationTokenSource();
        var resolutionToken = resolutionBudget.Token;
        resolutionBudget.Cancel();
        var method = typeof(InsideRuntimeClient).GetMethod(
            "ResolveAndSendRequest",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pending = Assert.IsAssignableFrom<Task>(method.Invoke(
            fixture.Runtime,
            [
                new ValueTask<ClusterIdentity>(resolution.Task),
                fixture.CreateTarget(InsideRuntimeClientMetaclusterFixture.RemoteCluster),
                fixture.CreateRequest(TestContext.Current.CancellationToken),
                completion,
                InvokeMethodOptions.None,
                resolutionBudget
            ]));

        resolution.SetResult(new ClusterIdentity(
            InsideRuntimeClientMetaclusterFixture.ServiceId,
            InsideRuntimeClientMetaclusterFixture.RemoteCluster));
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
    public async Task MetaclusterSend_RemoteFailure_CompletesExactlyOnce()
    {
        fixture.Reset();
        var expected = new InvalidOperationException("inside remote failure");
        fixture.Transport.Handler = _ => ValueTask.FromException<Response>(expected);
        var completion = new RecordingCompletion();

        fixture.Runtime.SendRequest(
            fixture.CreateTarget(InsideRuntimeClientMetaclusterFixture.RemoteCluster),
            fixture.CreateRequest(TestContext.Current.CancellationToken),
            completion,
            InvokeMethodOptions.None);
        await completion.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, completion.CompletionCount);
        Assert.Same(expected, completion.Response!.Exception);
        Assert.Equal(1, fixture.Transport.CallCount);
    }

    private static ConcurrentDictionary<(GrainId, CorrelationId), CallbackData> GetCallbacks(
        InsideRuntimeClient runtime) =>
        (ConcurrentDictionary<(GrainId, CorrelationId), CallbackData>)typeof(InsideRuntimeClient)
            .GetField("callbacks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(runtime)!;
}

[GrainType("phase6.target")]
public sealed class Phase6LocalRoutingGrain : Grain, IPhase6LocalRoutingGrain
{
    public Task<string> Echo(string value) => Task.FromResult($"local:{value}");
}

public interface IPhase6LocalRoutingGrain : IGrainWithStringKey
{
    Task<string> Echo(string value);
}

public sealed class InsideRuntimeClientMetaclusterFixture : IAsyncLifetime
{
    public const string ServiceId = "phase6-service";
    public const string HomeCluster = "home";
    public const string RemoteCluster = "remote";
    private const string LocatorName = "phase6-locator";
    private static readonly GrainType LocatedGrainType = GrainType.Create("phase6.located");
    private static readonly GrainType TargetGrainType = GrainType.Create("phase6.target");
    private readonly ServiceProvider _locatorServices;
    private InProcessTestCluster? _cluster;

    public InsideRuntimeClientMetaclusterFixture()
    {
        Transport = new RecordingTransport();
        Topology = new ControlledTopologyProvider(CreateTopology());
        Locator = new RecordingLocator();

        var locatedProperties = new GrainProperties(
            ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal, StringComparer.Ordinal)
                .Add(WellKnownGrainTypeProperties.ClusterLocator, LocatorName));
        var localProperties = new GrainProperties(
            ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal, StringComparer.Ordinal));
        var manifest = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty
                .Add(LocatedGrainType, locatedProperties)
                .Add(TargetGrainType, localProperties),
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        var manifestProvider = new FixedManifestProvider(
            new ClusterManifest(
            MajorMinorVersion.Zero,
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty,
            [manifest]),
            manifest);
        var propertiesResolver = new GrainPropertiesResolver(manifestProvider);
        var locatorServices = new ServiceCollection();
        locatorServices.AddKeyedSingleton<IClusterLocator>(LocatorName, Locator);
        _locatorServices = locatorServices.BuildServiceProvider();
        ReferenceResolver = new ClusterReferenceResolver(
            Options.Create(new ClusterOptions { ServiceId = ServiceId, ClusterId = HomeCluster }),
            Options.Create(new MetaclusterOptions { Enabled = true }),
            new ClusterLocatorResolver(propertiesResolver, _locatorServices),
            propertiesResolver,
            Topology,
            TimeProvider.System);
        BindingResolver = new UniversalReferenceBindingResolver(
            Options.Create(new ClusterOptions { ServiceId = ServiceId, ClusterId = HomeCluster }),
            Options.Create(new MetaclusterOptions { Enabled = true }),
            propertiesResolver);
    }

    internal InsideRuntimeClient Runtime { get; private set; } = null!;

    public IServiceProvider SiloServices { get; private set; } = null!;

    public RecordingTransport Transport { get; }

    public ControlledTopologyProvider Topology { get; }

    public RecordingLocator Locator { get; }

    private ClusterReferenceResolver ReferenceResolver { get; }

    private UniversalReferenceBindingResolver BindingResolver { get; }

    public async ValueTask InitializeAsync()
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.Options.ServiceId = ServiceId;
        builder.Options.ClusterId = HomeCluster;
        builder.ConfigureHost(hostBuilder =>
            TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration));
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.UseMetacluster();
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<IInterClusterTransport>(Transport);
                services.AddSingleton<IMetaclusterTopologyProvider>(Topology);
                services.AddSingleton(ReferenceResolver);
                services.AddSingleton(BindingResolver);
            });
        });
        _cluster = builder.Build();
        await _cluster.DeployAsync();
        SiloServices = _cluster.Silos[0].ServiceProvider;
        Runtime = SiloServices.GetRequiredService<InsideRuntimeClient>();
        Assert.Same(Transport, SiloServices.GetRequiredService<IInterClusterTransport>());
    }

    public async ValueTask DisposeAsync()
    {
        if (_cluster is not null)
        {
            await _cluster.DisposeAsync();
        }

        _locatorServices.Dispose();
    }

    public void Reset()
    {
        Transport.Reset();
        Locator.Reset();
        Topology.Reset(CreateTopology());
    }

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

        return (GrainReference)Runtime.InternalGrainFactory.GetGrain(
            UniversalReference.CreateCluster(grainId, interfaceType, serviceId, clusterId));
    }

    public GrainReference CreateVirtualTarget() =>
        (GrainReference)Runtime.InternalGrainFactory.GetGrain(
            UniversalReference.CreateVirtual(
                GrainId.Create(LocatedGrainType, "key"),
                default,
                ServiceId));

    public GrainReference CreateObserverTarget(string clusterId, string serviceId = ServiceId) =>
        (GrainReference)Runtime.InternalGrainFactory.GetGrain(
            UniversalReference.CreateCluster(
                ClientGrainId.Create().GrainId,
                default,
                serviceId,
                clusterId));

    public IInvokable CreateRequest(CancellationToken cancellationToken = default)
        => new TestInvokable(cancellationToken);

    public MetaclusterTopology CreateTopology(
        MetaclusterClusterState remoteState = MetaclusterClusterState.Active,
        long epoch = 6) =>
        new(
            ServiceId,
            epoch,
            ImmutableDictionary<string, MetaclusterCluster>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(HomeCluster, new MetaclusterCluster(HomeCluster, MetaclusterClusterState.Active, []))
                .Add(RemoteCluster, new MetaclusterCluster(RemoteCluster, remoteState, [])));
}

internal sealed class FixedManifestProvider(
    ClusterManifest current,
    GrainManifest localGrainManifest) : IClusterManifestProvider
{
    public ClusterManifest Current { get; } = current;

    public GrainManifest LocalGrainManifest { get; } = localGrainManifest;

    public IAsyncEnumerable<ClusterManifest> Updates => GetUpdates();

    private static async IAsyncEnumerable<ClusterManifest> GetUpdates()
    {
        await Task.CompletedTask;
        yield break;
    }
}

internal sealed class TestInvokable(CancellationToken cancellationToken) : IInvokable
{
    public bool IsCancellable => cancellationToken.CanBeCanceled;

    public object? GetTarget() => null;

    public void SetTarget(ITargetHolder holder)
    {
    }

    public ValueTask<Response> Invoke() => new(Response.Completed);

    public int GetArgumentCount() => 0;

    public object? GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));

    public void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(nameof(index));

    public string GetMethodName() => nameof(Invoke);

    public string GetInterfaceName() => typeof(TestInvokable).FullName!;

    public string GetActivityName() => $"{GetInterfaceName()}.{GetMethodName()}";

    public MethodInfo GetMethod() => typeof(TestInvokable).GetMethod(nameof(Invoke))!;

    public Type GetInterfaceType() => typeof(TestInvokable);

    public CancellationToken GetCancellationToken() => cancellationToken;

    public void Dispose()
    {
    }
}

public sealed class RecordingTransport : IInterClusterTransport
{
    private int _callCount;

    public Func<TransportCall, ValueTask<Response>> Handler { get; set; } =
        static _ => new ValueTask<Response>(Response.Completed);

    public TaskCompletionSource<TransportCall> Called { get; private set; } =
        NewCompletion();

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

    public void Reset()
    {
        Interlocked.Exchange(ref _callCount, 0);
        LastCall = null;
        Handler = static _ => new ValueTask<Response>(Response.Completed);
        Called = NewCompletion();
    }

    private static TaskCompletionSource<TransportCall> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public readonly record struct TransportCall(
    ClusterIdentity Destination,
    UniversalReference Target,
    IInvokable Request,
    InvokeMethodOptions Options,
    CancellationToken CancellationToken);

public sealed class RecordingCompletion : IResponseCompletionSource
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

public sealed class ControlledTopologyProvider(MetaclusterTopology current) : IMetaclusterTopologyProvider
{
    private TaskCompletionSource<MetaclusterTopology>? _nextRead;

    public MetaclusterTopology Current { get; set; } = current;

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

    public void Reset(MetaclusterTopology topology)
    {
        _nextRead = null;
        Current = topology;
    }
}

public sealed class RecordingLocator : IClusterLocator
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
            new ClusterLocation(InsideRuntimeClientMetaclusterFixture.RemoteCluster, 1, 6, false));
    }

    public void Reset()
    {
        CallCount = 0;
        LastContext = null;
    }
}

using System.Collections.Immutable;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.Serialization.Invocation;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.Messaging;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("Messaging")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[Trait("Phase", "5")]
public sealed class InterClusterRequestReceiverTests(TestEnvironmentFixture environment)
{
    [Fact]
    public async Task Receive_ValidRequest_AuthorizesValidatesOwnershipAndDispatches()
    {
        using var fixture = new ReceiverFixture(environment);
        var expected = Response.FromResult(42);
        fixture.Response = expected;

        var actual = await fixture.Receive();

        Assert.Same(expected, actual);
        Assert.Equal(
            ["authorize", "topology", "ownership", "factory", "dispatch"],
            fixture.Calls);
        Assert.Equal(fixture.Target.GrainId, fixture.Ownership.LastGrainId);
        Assert.Equal("local", fixture.Ownership.LastClusterId);
        Assert.Equal("local", fixture.BoundTarget!.Value.ClusterId);
        expected.Dispose();
    }

    [Fact]
    public async Task Receive_ServiceMismatch_RejectsBeforeAuthorizer()
    {
        using var fixture = new ReceiverFixture(environment);
        var wrongSource = new ClusterIdentity("other-service", "source");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Receive(source: wrongSource).AsTask());

        Assert.Contains("must match local service 'service'", exception.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task Receive_RejectingAuthorizer_RejectsBeforeTopologyOwnershipOrDispatch()
    {
        using var fixture = new ReceiverFixture(environment);
        var expected = new UnauthorizedAccessException("denied by application");
        fixture.Authorizer.Authorize(
                Arg.Any<ClusterIdentity>(),
                Arg.Any<UniversalReference>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                fixture.Calls.Add("authorize");
                return ValueTask.FromException(expected);
            });

        var actual = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Receive().AsTask());

        Assert.Same(expected, actual);
        Assert.Equal(["authorize"], fixture.Calls);
    }

    [Fact]
    public async Task RejectingInterClusterRequestAuthorizer_RejectsEveryRequest()
    {
        var authorizer = new RejectingInterClusterRequestAuthorizer();
        var source = new ClusterIdentity("service", "source");

        var first = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => authorizer.Authorize(source, ReceiverFixture.CreateTarget("local")).AsTask());
        var second = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => authorizer.Authorize(
                new ClusterIdentity("service", "other"),
                ReceiverFixture.CreateTarget("local")).AsTask());

        Assert.Equal(first.Message, second.Message.Replace("'service/other'", "'service/source'", StringComparison.Ordinal));
        Assert.Contains(nameof(IInterClusterRequestAuthorizer), first.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receive_SpoofedSourceCluster_RejectsBeforeDispatch()
    {
        using var fixture = new ReceiverFixture(environment);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Receive(source: new ClusterIdentity("service", "spoofed")).AsTask());

        Assert.Contains("spoofed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["authorize", "topology"], fixture.Calls);
    }

    [Fact]
    public async Task Receive_UnknownOrRemovedDestinationCluster_Rejects()
    {
        using var fixture = new ReceiverFixture(environment);
        fixture.SetSourceState(MetaclusterClusterState.Removed);

        var removed = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Receive().AsTask());
        Assert.Equal(["authorize", "topology"], fixture.Calls);
        fixture.Calls.Clear();
        fixture.SetSourceState(MetaclusterClusterState.Active);
        var wrongDestination = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Receive(target: ReceiverFixture.CreateTarget("unknown")).AsTask());

        Assert.Contains("source", removed.Message, StringComparison.Ordinal);
        Assert.Contains("unknown", wrongDestination.Message, StringComparison.Ordinal);
        Assert.Equal(["authorize", "topology"], fixture.Calls);
    }

    [Fact]
    public async Task Receive_RemovedLocalCluster_RejectsBeforeTargetValidationOrDispatch()
    {
        using var fixture = new ReceiverFixture(environment);
        fixture.SetLocalState(MetaclusterClusterState.Removed);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Receive().AsTask());

        Assert.Contains("Local cluster 'local'", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["authorize", "topology"], fixture.Calls);
        Assert.Equal(0, fixture.Ownership.ValidationCount);
        fixture.Runtime.DidNotReceiveWithAnyArgs().SendRequest(default!, default!, default, default);
    }

    [Fact]
    public async Task Receive_VirtualTarget_RejectsBeforeOwnershipLookupOrDispatch()
    {
        using var fixture = new ReceiverFixture(environment);
        var target = UniversalReference.CreateVirtual(
            fixture.Target.GrainId,
            fixture.Target.InterfaceType,
            fixture.Target.ServiceId);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Receive(target: target).AsTask());

        Assert.Contains("must be cluster-bound", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["authorize", "topology"], fixture.Calls);
        Assert.Equal(0, fixture.Ownership.ValidationCount);
        fixture.Runtime.DidNotReceiveWithAnyArgs().SendRequest(default!, default!, default, default);
    }

    [Fact]
    public async Task Receive_DrainingSourceCluster_RemainsEligibleForDispatch()
    {
        using var fixture = new ReceiverFixture(environment);
        fixture.SetSourceState(MetaclusterClusterState.Draining);

        var response = await fixture.Receive();

        Assert.Same(Response.Completed, response);
        Assert.Equal(
            ["authorize", "topology", "ownership", "factory", "dispatch"],
            fixture.Calls);
        Assert.Equal(1, fixture.Ownership.ValidationCount);
    }

    [Fact]
    public async Task Receive_WrongTargetInterface_RejectsBeforeOwnershipLookup()
    {
        using var fixture = new ReceiverFixture(environment);
        var target = UniversalReference.CreateCluster(
            fixture.Target.GrainId,
            GrainInterfaceType.Create("wrong-interface"),
            "service",
            "local");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Receive(target: target).AsTask());

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["authorize", "topology"], fixture.Calls);
        Assert.Equal(0, fixture.Ownership.ValidationCount);
    }

    [Fact]
    public async Task Receive_DefaultTargetInterface_RejectsBeforeOwnershipLookup()
    {
        using var fixture = new ReceiverFixture(environment);
        var target = UniversalReference.CreateCluster(
            fixture.Target.GrainId,
            default,
            "service",
            "local");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Receive(target: target).AsTask());

        Assert.Contains("must identify the interface", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["authorize", "topology"], fixture.Calls);
        Assert.Equal(0, fixture.Ownership.ValidationCount);
    }

    [Fact]
    public async Task Receive_ForgedInvocationOptions_RejectsBeforeOwnershipLookup()
    {
        using var fixture = new ReceiverFixture(environment);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Receive(options: InvokeMethodOptions.AlwaysInterleave).AsTask());

        Assert.Contains("do not match trusted request options", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["authorize", "topology"], fixture.Calls);
        Assert.Equal(0, fixture.Ownership.ValidationCount);
    }

    [Fact]
    public async Task Receive_UnexportedSystemTarget_RejectsByDefault()
    {
        using var fixture = new ReceiverFixture(environment);
        var (target, request) = fixture.CreateSystemTarget();

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Receive(target: target, request: request).AsTask());

        Assert.Contains("not exported", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["authorize", "topology"], fixture.Calls);
        fixture.Runtime.DidNotReceiveWithAnyArgs().SendRequest(default!, default!, default, default);
    }

    [Fact]
    public async Task Receive_ExportedSystemTarget_AllowsOnlyConfiguredInterface()
    {
        using var fixture = new ReceiverFixture(environment);
        var (target, request) = fixture.CreateSystemTarget();
        fixture.Options.ExportedSystemTargets.Add(target.GrainId.Type.ToString());

        var response = await fixture.Receive(target: target, request: request);
        var wrongRequest = fixture.CreateRequest(typeof(ISimpleGrain));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Receive(target: target, request: wrongRequest).AsTask());

        Assert.Same(Response.Completed, response);
        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        fixture.Runtime.Received(1).SendRequest(
            Arg.Any<GrainReference>(),
            request,
            Arg.Any<IResponseCompletionSource>(),
            InvokeMethodOptions.None);
    }

    [Fact]
    public async Task Receive_ExportedSystemTarget_RejectsUnknownOrInactiveLocalSilo()
    {
        using var fixture = new ReceiverFixture(environment);
        var unknownSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 32146), 18);
        var (unknownTarget, request) = fixture.CreateSystemTarget(unknownSilo);
        fixture.Options.ExportedSystemTargets.Add(unknownTarget.GrainId.Type.ToString());

        var unknown = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Receive(target: unknownTarget, request: request).AsTask());

        fixture.SetSystemTargetSiloStatus(unknownSilo, SiloStatus.Dead);
        var inactive = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Receive(target: unknownTarget, request: request).AsTask());

        Assert.Contains("active member", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("active member", inactive.Message, StringComparison.Ordinal);
        fixture.Runtime.DidNotReceiveWithAnyArgs().SendRequest(default!, default!, default, default);
    }

    [Fact]
    public async Task Receive_GrainWithStaleOwnership_RejectsBeforeDispatch()
    {
        using var fixture = new ReceiverFixture(environment);
        var expected = new InvalidOperationException("stale owner");
        fixture.Ownership.Failure = expected;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Receive().AsTask());

        Assert.Same(expected, actual);
        Assert.Equal(["authorize", "topology", "ownership"], fixture.Calls);
        fixture.Runtime.DidNotReceiveWithAnyArgs().SendRequest(default!, default!, default, default);
    }

    [Fact]
    public async Task Receive_OneWayRequest_DispatchesWithoutResponsePayload()
    {
        using var fixture = new ReceiverFixture(environment);
        var request = fixture.CreateRequest(typeof(ISimpleGrain), InvokeMethodOptions.OneWay);

        var actual = await fixture.Receive(request: request, options: InvokeMethodOptions.OneWay);

        Assert.Same(Response.Completed, actual);
        fixture.Runtime.Received(1).SendRequest(
            Arg.Any<GrainReference>(),
            request,
            null,
            InvokeMethodOptions.OneWay);
        Assert.Equal(["authorize", "topology", "ownership", "factory", "dispatch"], fixture.Calls);
    }

    [Fact]
    public async Task Receive_RequestContext_IsImportedForDispatchAndRestoredAfterward()
    {
        RequestContext.Clear();
        RequestContext.Set("phase5", "inbound");
        try
        {
            using var fixture = new ReceiverFixture(environment);
            object? observed = null;
            fixture.OnDispatch = () => observed = RequestContext.Get("phase5");

            await fixture.Receive();

            Assert.Equal("inbound", observed);
            Assert.Equal("inbound", RequestContext.Get("phase5"));
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    [Fact]
    public async Task Receive_HandlerResponse_IsReturnedUnchanged()
    {
        using var fixture = new ReceiverFixture(environment);
        var expected = Response.FromResult("remote-result");
        fixture.Response = expected;

        var actual = await fixture.Receive();

        Assert.Same(expected, actual);
        Assert.Equal("remote-result", actual.GetResult<string>());
        expected.Dispose();
    }

    [Fact]
    public async Task Receive_HandlerException_IsReturnedAsRemoteFailure()
    {
        using var fixture = new ReceiverFixture(environment);
        var expected = new ApplicationException("handler failed");
        fixture.Response = Response.FromException(expected);

        var actual = await Assert.ThrowsAsync<ApplicationException>(
            () => fixture.Receive().AsTask());

        Assert.Same(expected, actual);
        Assert.Equal(["authorize", "topology", "ownership", "factory", "dispatch"], fixture.Calls);
    }

    [Fact]
    public async Task Receive_CallerCancellationToken_IsInjectedIntoInvokableArguments()
    {
        using var fixture = new ReceiverFixture(environment);
        using var cancellation = new CancellationTokenSource();
        var request = fixture.CreateCancellableRequest();

        await fixture.Receive(request: request, cancellationToken: cancellation.Token);

        request.Received(1).SetArgument(1, cancellation.Token);
        request.DidNotReceive().SetArgument(0, Arg.Any<object>());
        fixture.Runtime.Received(1).SendRequest(
            Arg.Any<GrainReference>(),
            request,
            Arg.Any<IResponseCompletionSource>(),
            InvokeMethodOptions.None);
    }

    [Fact]
    public async Task Receive_CancellationBeforeAuthorization_DoesNotDispatch()
    {
        using var fixture = new ReceiverFixture(environment);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Receive(cancellationToken: cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task Receive_CancellationDuringDispatch_CancelsLocalInvocation()
    {
        using var fixture = new ReceiverFixture(environment);
        fixture.AutoComplete = false;
        using var cancellation = new CancellationTokenSource();
        var request = fixture.CreateCancellableRequest();
        var pending = fixture.Receive(
            request: request,
            cancellationToken: cancellation.Token).AsTask();
        await fixture.DispatchEntered.Task;

        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        request.Received(1).SetArgument(1, cancellation.Token);
        Assert.Single(fixture.Runtime.ReceivedCalls());
    }

    [Fact]
    public async Task Receive_TargetServiceMismatch_RejectsBeforeAuthorizer()
    {
        using var fixture = new ReceiverFixture(environment);
        var target = UniversalReference.CreateCluster(
            fixture.Target.GrainId,
            fixture.Target.InterfaceType,
            "other-service",
            "local");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Receive(target: target).AsTask());

        Assert.Contains("target service 'other-service'", exception.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Calls);
        Assert.Equal(0, fixture.Ownership.ValidationCount);
        fixture.Runtime.DidNotReceiveWithAnyArgs().SendRequest(default!, default!, default, default);
    }

    [Fact]
    public async Task Receive_RequestWithoutTrustedMetadata_RejectsBeforeOwnershipLookup()
    {
        using var fixture = new ReceiverFixture(environment);
        var request = Substitute.For<IInvokable>();
        request.GetInterfaceType().Returns(typeof(ISimpleGrain));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Receive(request: request).AsTask());

        Assert.Contains("does not expose trusted invocation metadata", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["authorize", "topology"], fixture.Calls);
        Assert.Equal(0, fixture.Ownership.ValidationCount);
        fixture.Runtime.DidNotReceiveWithAnyArgs().SendRequest(default!, default!, default, default);
    }

    [Fact]
    public async Task Receive_ExportedSystemTarget_RequiresActiveExactGenerationLocalMember()
    {
        using var fixture = new ReceiverFixture(environment);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 32147);
        var exactSilo = SiloAddress.New(endpoint, 42);
        var staleGeneration = SiloAddress.New(endpoint, 41);
        var (target, request) = fixture.CreateSystemTarget(exactSilo);
        fixture.Options.ExportedSystemTargets.Add(target.GrainId.Type.ToString());
        fixture.SetSystemTargetSiloStatus(staleGeneration, SiloStatus.Active);

        var rejected = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Receive(target: target, request: request).AsTask());

        Assert.Contains("active member", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(exactSilo.Endpoint, staleGeneration.Endpoint);
        Assert.NotEqual(exactSilo.Generation, staleGeneration.Generation);
        fixture.Runtime.DidNotReceiveWithAnyArgs().SendRequest(default!, default!, default, default);

        fixture.SetSystemTargetSiloStatus(exactSilo, SiloStatus.Active);
        var accepted = await fixture.Receive(target: target, request: request);

        Assert.Same(Response.Completed, accepted);
        fixture.Runtime.Received(1).SendRequest(
            Arg.Any<GrainReference>(),
            request,
            Arg.Any<IResponseCompletionSource>(),
            InvokeMethodOptions.None);
    }

    [Fact]
    public void CsCheck_IngressInvalidSecurityShapes_NeverReachOwnershipOrDispatch()
    {
        CsCheck.Gen.Int
            .Select(static value => 1 + (int)((uint)value % 63))
            .Sample(
                mask => VerifyInvalidIngressShape(mask).GetAwaiter().GetResult(),
                seed: "phase4-ingress-security-shapes-v1",
                iter: 64,
                threads: 1,
                print: static mask => $"invalid-shape-mask=0x{mask:X2}");
    }

    private async Task VerifyInvalidIngressShape(int mask)
    {
        using var fixture = new ReceiverFixture(environment);
        var source = new ClusterIdentity(
            (mask & 0x01) == 0 ? "service" : "other-service",
            "source");
        var targetService = (mask & 0x02) == 0 ? "service" : "other-service";
        var targetCluster = (mask & 0x04) == 0 ? "local" : "unknown";
        var interfaceType = (mask & 0x08) == 0 ? fixture.Target.InterfaceType : default;
        var target = (mask & 0x10) == 0
            ? UniversalReference.CreateCluster(
                fixture.Target.GrainId,
                interfaceType,
                targetService,
                targetCluster)
            : UniversalReference.CreateVirtual(
                fixture.Target.GrainId,
                interfaceType,
                targetService);
        var options = (mask & 0x20) == 0
            ? InvokeMethodOptions.None
            : InvokeMethodOptions.AlwaysInterleave;

        var exception = await Record.ExceptionAsync(
            () => fixture.Receive(source, target, fixture.Request, options).AsTask());

        Assert.True(
            exception is InvalidOperationException or UnauthorizedAccessException,
            $"mask=0x{mask:X2}; exception={exception}");
        Assert.Equal(0, fixture.Ownership.ValidationCount);
        fixture.Runtime.DidNotReceiveWithAnyArgs().SendRequest(default!, default!, default, default);
    }

    private interface ICancellableCall
    {
        Task Invoke(int value, CancellationToken cancellationToken);
    }

    private sealed class ReceiverFixture : IDisposable
    {
        private const string LocatorName = "phase5-owner";
        private static readonly GrainType LocatedGrainType = GrainType.Create("phase5-grain");
        private static readonly SiloAddress SystemTargetSilo = SiloAddress.New(
            new IPEndPoint(IPAddress.Loopback, 32145),
            17);
        private readonly ServiceProvider _services;
        private readonly GrainInterfaceTypeResolver _interfaceResolver;
        private readonly InterClusterRequestReceiver _receiver;
        private MetaclusterClusterState _localState = MetaclusterClusterState.Active;
        private MetaclusterClusterState _sourceState = MetaclusterClusterState.Active;
        private MetaclusterTopology _topology;

        public ReceiverFixture(TestEnvironmentFixture environment)
        {
            Calls = [];
            Options = new MetaclusterOptions { Enabled = true };
            Authorizer = Substitute.For<IInterClusterRequestAuthorizer>();
            Authorizer.Authorize(
                    Arg.Any<ClusterIdentity>(),
                    Arg.Any<UniversalReference>(),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Calls.Add("authorize");
                    return ValueTask.CompletedTask;
                });
            TopologyProvider = Substitute.For<IMetaclusterTopologyProvider>();
            _topology = CreateTopology(_sourceState, _localState);
            TopologyProvider.GetTopology(Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Calls.Add("topology");
                    return new ValueTask<MetaclusterTopology>(_topology);
                });
            Ownership = new RecordingOwnershipValidator(Calls);

            var properties = new GrainProperties(
                ImmutableDictionary<string, string>.Empty
                    .WithComparers(StringComparer.Ordinal, StringComparer.Ordinal)
                    .Add(WellKnownGrainTypeProperties.ClusterLocator, LocatorName));
            var manifest = new GrainManifest(
                ImmutableDictionary<GrainType, GrainProperties>.Empty.Add(LocatedGrainType, properties),
                ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
            var clusterManifest = new ClusterManifest(
                MajorMinorVersion.Zero,
                ImmutableDictionary<SiloAddress, GrainManifest>.Empty,
                [manifest]);
            var manifestProvider = Substitute.For<IClusterManifestProvider>();
            manifestProvider.Current.Returns(clusterManifest);
            manifestProvider.LocalGrainManifest.Returns(manifest);
            var propertiesResolver = new GrainPropertiesResolver(manifestProvider);
            var services = new ServiceCollection();
            services.AddKeyedSingleton<IClusterLocator>(LocatorName, Ownership);
            _services = services.BuildServiceProvider();
            var locatorResolver = new ClusterLocatorResolver(propertiesResolver, _services);

            var metaclusterOptions = Microsoft.Extensions.Options.Options.Create(Options);
            var bindingResolver = new UniversalReferenceBindingResolver(
                Microsoft.Extensions.Options.Options.Create(
                    new ClusterOptions { ServiceId = "service", ClusterId = "local" }),
                metaclusterOptions,
                propertiesResolver);
            _interfaceResolver = environment.Services.GetRequiredService<GrainInterfaceTypeResolver>();
            Target = CreateTarget("local", _interfaceResolver.GetGrainInterfaceType(typeof(ISimpleGrain)));
            Request = CreateRequest(typeof(ISimpleGrain));
            Source = new ClusterIdentity("service", "source");
            MembershipService = Substitute.For<IClusterMembershipService>();
            SetSystemTargetSiloStatus(SystemTargetSilo, SiloStatus.Active);

            var reference = (GrainReference)environment.InternalGrainFactory
                .GetGrain(Target.GrainId);
            GrainFactory = Substitute.For<IInternalGrainFactory>();
            GrainFactory.GetGrain(Arg.Any<UniversalReference>())
                .Returns(call =>
                {
                    Calls.Add("factory");
                    BoundTarget = call.Arg<UniversalReference>();
                    return reference;
                });
            Runtime = Substitute.For<IRuntimeClient>();
            Runtime.When(runtime => runtime.SendRequest(
                    Arg.Any<GrainReference>(),
                    Arg.Any<IInvokable>(),
                    Arg.Any<IResponseCompletionSource?>(),
                    Arg.Any<InvokeMethodOptions>()))
                .Do(call =>
                {
                    Calls.Add("dispatch");
                    DispatchEntered.TrySetResult();
                    OnDispatch?.Invoke();
                    if (AutoComplete && call.ArgAt<IResponseCompletionSource?>(2) is { } completion)
                    {
                        completion.Complete(Response);
                    }
                });
            _receiver = new InterClusterRequestReceiver(
                GrainFactory,
                Runtime,
                bindingResolver,
                metaclusterOptions,
                locatorResolver,
                TopologyProvider,
                _interfaceResolver,
                Authorizer,
                MembershipService);
        }

        public List<string> Calls { get; }

        public MetaclusterOptions Options { get; }

        public IInterClusterRequestAuthorizer Authorizer { get; }

        public IClusterMembershipService MembershipService { get; }

        public IMetaclusterTopologyProvider TopologyProvider { get; }

        public RecordingOwnershipValidator Ownership { get; }

        public IInternalGrainFactory GrainFactory { get; }

        public IRuntimeClient Runtime { get; }

        public ClusterIdentity Source { get; }

        public UniversalReference Target { get; }

        public IInvokable Request { get; }

        public UniversalReference? BoundTarget { get; private set; }

        public Response Response { get; set; } = Response.Completed;

        public bool AutoComplete { get; set; } = true;

        public Action? OnDispatch { get; set; }

        public TaskCompletionSource DispatchEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetSourceState(MetaclusterClusterState state)
        {
            _sourceState = state;
            _topology = CreateTopology(_sourceState, _localState);
        }

        public void SetLocalState(MetaclusterClusterState state)
        {
            _localState = state;
            _topology = CreateTopology(_sourceState, _localState);
        }

        public void SetSystemTargetSiloStatus(SiloAddress siloAddress, SiloStatus status)
        {
            var members = ImmutableDictionary<SiloAddress, ClusterMember>.Empty;
            if (status != SiloStatus.None)
            {
                members = members.Add(
                    siloAddress,
                    new ClusterMember(siloAddress, status, siloAddress.ToParsableString()));
            }

            MembershipService.CurrentSnapshot.Returns(
                new ClusterMembershipSnapshot(members, new MembershipVersion(1)));
        }

        public ValueTask<Response> Receive(
            ClusterIdentity? source = null,
            UniversalReference? target = null,
            IInvokable? request = null,
            InvokeMethodOptions options = InvokeMethodOptions.None,
            CancellationToken cancellationToken = default) =>
            _receiver.ReceiveRequest(
                source ?? Source,
                target ?? Target,
                request ?? Request,
                options,
                cancellationToken);

        public IRequest CreateRequest(
            Type interfaceType,
            InvokeMethodOptions options = InvokeMethodOptions.None)
        {
            var request = Substitute.For<IRequest>();
            request.GetInterfaceType().Returns(interfaceType);
            request.Options.Returns(options);
            return request;
        }

        public IRequest CreateCancellableRequest()
        {
            var request = CreateRequest(typeof(ISimpleGrain));
            request.IsCancellable.Returns(true);
            request.GetMethod().Returns(typeof(ICancellableCall).GetMethod(nameof(ICancellableCall.Invoke))!);
            return request;
        }

        public (UniversalReference Target, IInvokable Request) CreateSystemTarget(SiloAddress? silo = null)
        {
            var grainId = SystemTargetGrainId.Create(
                Constants.SiloControlType,
                silo ?? SystemTargetSilo,
                "phase5").GrainId;
            var interfaceType = _interfaceResolver.GetGrainInterfaceType(typeof(ISiloControl));
            return (
                UniversalReference.CreateCluster(grainId, interfaceType, "service", "local"),
                CreateRequest(typeof(ISiloControl)));
        }

        public static UniversalReference CreateTarget(string clusterId) =>
            CreateTarget(clusterId, GrainInterfaceType.Create("interface"));

        private static UniversalReference CreateTarget(
            string clusterId,
            GrainInterfaceType interfaceType) =>
            UniversalReference.CreateCluster(
                GrainId.Create(LocatedGrainType, "receiver-key"),
                interfaceType,
                "service",
                clusterId);

        private static MetaclusterTopology CreateTopology(
            MetaclusterClusterState sourceState,
            MetaclusterClusterState localState) =>
            new(
                "service",
                7,
                ImmutableDictionary<string, MetaclusterCluster>.Empty
                    .WithComparers(StringComparer.Ordinal)
                    .Add("source", new MetaclusterCluster("source", sourceState, []))
                    .Add("local", new MetaclusterCluster(
                        "local",
                        localState,
                        [])));

        public void Dispose() => _services.Dispose();
    }

    private sealed class RecordingOwnershipValidator(List<string> calls)
        : IClusterLocator, IClusterOwnershipValidator
    {
        public Exception? Failure { get; set; }

        public int ValidationCount { get; private set; }

        public GrainId LastGrainId { get; private set; }

        public string? LastClusterId { get; private set; }

        public ValueTask<ClusterLocation> Locate(
            GrainId grainId,
            ClusterLocationContext context,
            CancellationToken cancellationToken = default) =>
            new(new ClusterLocation("local", 7, 1, false));

        public ValueTask<ClusterDirectoryEntry> ValidateLocalOwnership(
            GrainId grainId,
            string localClusterId,
            CancellationToken cancellationToken = default)
        {
            calls.Add("ownership");
            ValidationCount++;
            LastGrainId = grainId;
            LastClusterId = localClusterId;
            if (Failure is { } failure)
            {
                return ValueTask.FromException<ClusterDirectoryEntry>(failure);
            }

            return new ValueTask<ClusterDirectoryEntry>(
                new ClusterDirectoryEntry(
                    grainId,
                    localClusterId,
                    1,
                    7,
                    1,
                    new DateTimeOffset(2040, 1, 1, 0, 1, 0, TimeSpan.Zero)));
        }
    }
}

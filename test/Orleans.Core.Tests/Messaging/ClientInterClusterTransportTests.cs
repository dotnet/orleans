using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using TestExtensions;
using Xunit;

namespace UnitTests.Messaging;

[TestArea("Messaging")]
[TestCategory("BVT")]
[TestSuite("BVT")]
public sealed class ClientInterClusterTransportTests
{
    [Fact]
    public async Task ForwardsRequestThroughDestinationClient()
    {
        var destination = new ClusterIdentity("service", "remote");
        var target = UniversalReference.CreateVirtual(
            GrainId.Create("grain", "key"),
            GrainInterfaceType.Create("interface"),
            "service");
        var routedTarget = UniversalReference.CreateCluster(
            target.GrainId,
            target.InterfaceType,
            target.ServiceId,
            "remote");
        var request = Substitute.For<IInvokable>();
        var relay = Substitute.For<IInterClusterRelay>();
        var client = Substitute.For<IClusterClient>();
        var response = Response.FromResult(42);
        client.GetGrain<IInterClusterRelay>("local").Returns(relay);
        relay.Forward(
                new ClusterIdentity("service", "local"),
                routedTarget,
                request,
                InvokeMethodOptions.None,
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Response>(response));
        var provider = new TestClientProvider(client);
        var transport = new ClientInterClusterTransport(
            provider,
            Options.Create(new ClusterOptions { ServiceId = "service", ClusterId = "local" }));

        var actual = await transport.SendRequest(
            destination,
            target,
            request,
            InvokeMethodOptions.None);

        Assert.Same(response, actual);
        await provider.ReceivedDestination;
        Assert.Equal(destination, provider.Destination);
        response.Dispose();
    }

    private sealed class TestClientProvider(IClusterClient client) : IInterClusterClientProvider
    {
        private readonly TaskCompletionSource _receivedDestination = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ClusterIdentity Destination { get; private set; }

        public Task ReceivedDestination => _receivedDestination.Task;

        public ValueTask<IClusterClient> GetClient(
            ClusterIdentity destination,
            CancellationToken cancellationToken = default)
        {
            Destination = destination;
            _receivedDestination.SetResult();
            return new ValueTask<IClusterClient>(client);
        }
    }

    [Fact]
    [Trait("Phase", "5")]
    public async Task Send_VirtualReference_BindsToDestinationAndInvokesRelay()
    {
        var destination = new ClusterIdentity("service", "remote");
        var target = CreateVirtualTarget();
        var request = Substitute.For<IInvokable>();
        var relay = Substitute.For<IInterClusterRelay>();
        var client = Substitute.For<IClusterClient>();
        var expected = Response.FromResult("accepted");
        client.GetGrain<IInterClusterRelay>("local").Returns(relay);
        relay.Forward(
                new ClusterIdentity("service", "local"),
                Arg.Is<UniversalReference>(value =>
                    value.Binding == UniversalReferenceBinding.Cluster
                    && value.ClusterId == "remote"
                    && value.GrainId == target.GrainId
                    && value.InterfaceType == target.InterfaceType),
                request,
                InvokeMethodOptions.ReadOnly,
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Response>(expected));
        var provider = Substitute.For<IInterClusterClientProvider>();
        provider.GetClient(destination, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IClusterClient>(client));
        var transport = CreateTransport(provider);

        var actual = await transport.SendRequest(
            destination,
            target,
            request,
            InvokeMethodOptions.ReadOnly);

        Assert.Same(expected, actual);
        await provider.Received(1).GetClient(destination, Arg.Any<CancellationToken>());
        await relay.Received(1).Forward(
            new ClusterIdentity("service", "local"),
            Arg.Any<UniversalReference>(),
            request,
            InvokeMethodOptions.ReadOnly,
            Arg.Any<CancellationToken>());
        expected.Dispose();
    }

    [Fact]
    [Trait("Phase", "5")]
    public async Task Send_ClusterBoundReferenceToDestination_PassesThrough()
    {
        var destination = new ClusterIdentity("service", "remote");
        var target = CreateClusterTarget("remote");
        var request = Substitute.For<IInvokable>();
        var relay = Substitute.For<IInterClusterRelay>();
        var client = Substitute.For<IClusterClient>();
        client.GetGrain<IInterClusterRelay>("local").Returns(relay);
        relay.Forward(
                Arg.Any<ClusterIdentity>(),
                target,
                request,
                InvokeMethodOptions.Unordered,
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Response>(Response.Completed));
        var provider = Substitute.For<IInterClusterClientProvider>();
        provider.GetClient(destination, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IClusterClient>(client));

        var actual = await CreateTransport(provider).SendRequest(
            destination,
            target,
            request,
            InvokeMethodOptions.Unordered);

        Assert.Same(Response.Completed, actual);
        await relay.Received(1).Forward(
            new ClusterIdentity("service", "local"),
            Arg.Is<UniversalReference>(value => value.Equals(target)),
            request,
            InvokeMethodOptions.Unordered,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Phase", "5")]
    public async Task Send_ClusterBoundReferenceToDifferentCluster_IsRejected()
    {
        var provider = Substitute.For<IInterClusterClientProvider>();
        var transport = CreateTransport(provider);

        var exception = await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => transport.SendRequest(
                new ClusterIdentity("service", "west"),
                CreateClusterTarget("east"),
                Substitute.For<IInvokable>(),
                InvokeMethodOptions.None).AsTask());

        Assert.Contains("east", exception.Message, System.StringComparison.Ordinal);
        Assert.Contains("west", exception.Message, System.StringComparison.Ordinal);
        await provider.DidNotReceiveWithAnyArgs().GetClient(default, default);
    }

    [Fact]
    [Trait("Phase", "5")]
    public async Task Send_SourceOrDestinationServiceMismatch_IsRejected()
    {
        var provider = Substitute.For<IInterClusterClientProvider>();
        var transport = CreateTransport(provider);

        var destinationMismatch = await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => transport.SendRequest(
                new ClusterIdentity("other-service", "remote"),
                CreateVirtualTarget(),
                Substitute.For<IInvokable>(),
                InvokeMethodOptions.None).AsTask());
        var targetMismatch = await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => transport.SendRequest(
                new ClusterIdentity("service", "remote"),
                UniversalReference.CreateVirtual(
                    GrainId.Create("grain", "key"),
                    GrainInterfaceType.Create("interface"),
                    "other-service"),
                Substitute.For<IInvokable>(),
                InvokeMethodOptions.None).AsTask());

        Assert.Contains("local service 'service'", destinationMismatch.Message, System.StringComparison.Ordinal);
        Assert.Contains("local service 'service'", targetMismatch.Message, System.StringComparison.Ordinal);
        await provider.DidNotReceiveWithAnyArgs().GetClient(default, default);
    }

    [Fact]
    [Trait("Phase", "5")]
    public async Task Send_ProviderFailure_PropagatesWithoutRelayInvocation()
    {
        var expected = new System.InvalidOperationException("provider failed");
        var provider = Substitute.For<IInterClusterClientProvider>();
        provider.GetClient(Arg.Any<ClusterIdentity>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<IClusterClient>(expected));
        var transport = CreateTransport(provider);

        var actual = await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => transport.SendRequest(
                new ClusterIdentity("service", "remote"),
                CreateVirtualTarget(),
                Substitute.For<IInvokable>(),
                InvokeMethodOptions.None).AsTask());

        Assert.Same(expected, actual);
        await provider.Received(1).GetClient(
            new ClusterIdentity("service", "remote"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Phase", "5")]
    public async Task Send_RelayFailure_Propagates()
    {
        var expected = new System.ApplicationException("relay failed");
        var relay = Substitute.For<IInterClusterRelay>();
        relay.Forward(
                Arg.Any<ClusterIdentity>(),
                Arg.Any<UniversalReference>(),
                Arg.Any<IInvokable>(),
                Arg.Any<InvokeMethodOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<Response>(expected));
        var client = Substitute.For<IClusterClient>();
        client.GetGrain<IInterClusterRelay>("local").Returns(relay);
        var provider = Substitute.For<IInterClusterClientProvider>();
        provider.GetClient(Arg.Any<ClusterIdentity>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IClusterClient>(client));

        var actual = await Assert.ThrowsAsync<System.ApplicationException>(
            () => CreateTransport(provider).SendRequest(
                new ClusterIdentity("service", "remote"),
                CreateVirtualTarget(),
                Substitute.For<IInvokable>(),
                InvokeMethodOptions.None).AsTask());

        Assert.Same(expected, actual);
        await relay.Received(1).Forward(
            new ClusterIdentity("service", "local"),
            Arg.Any<UniversalReference>(),
            Arg.Any<IInvokable>(),
            InvokeMethodOptions.None,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Phase", "5")]
    public async Task Send_CallerCancellation_CancelsProviderOrRelay()
    {
        var providerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new DelegatingClientProvider((_, token) =>
            AwaitCancellation<IClusterClient>(token, providerEntered));
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = CreateTransport(provider).SendRequest(
                new ClusterIdentity("service", "remote"),
                CreateVirtualTarget(),
                Substitute.For<IInvokable>(),
                InvokeMethodOptions.None,
                cancellation.Token).AsTask();
            await providerEntered.Task;
            cancellation.Cancel();

            var exception = await Assert.ThrowsAnyAsync<System.OperationCanceledException>(() => pending);
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }

        var relayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var relay = Substitute.For<IInterClusterRelay>();
        relay.Forward(
                Arg.Any<ClusterIdentity>(),
                Arg.Any<UniversalReference>(),
                Arg.Any<IInvokable>(),
                Arg.Any<InvokeMethodOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(call => AwaitCancellation<Response>(
                call.ArgAt<CancellationToken>(4),
                relayEntered));
        var client = Substitute.For<IClusterClient>();
        client.GetGrain<IInterClusterRelay>("local").Returns(relay);
        var immediateProvider = new DelegatingClientProvider(
            (_, _) => new ValueTask<IClusterClient>(client));
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = CreateTransport(immediateProvider).SendRequest(
                new ClusterIdentity("service", "remote"),
                CreateVirtualTarget(),
                Substitute.For<IInvokable>(),
                InvokeMethodOptions.None,
                cancellation.Token).AsTask();
            await relayEntered.Task;
            cancellation.Cancel();

            var exception = await Assert.ThrowsAnyAsync<System.OperationCanceledException>(() => pending);
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }
    }

    [Fact]
    [Trait("Phase", "5")]
    public async Task Send_OneWayRequest_CompletesAfterRelayAcceptance()
    {
        var accepted = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        var relay = Substitute.For<IInterClusterRelay>();
        relay.Forward(
                Arg.Any<ClusterIdentity>(),
                Arg.Any<UniversalReference>(),
                Arg.Any<IInvokable>(),
                InvokeMethodOptions.OneWay,
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Response>(accepted.Task));
        var client = Substitute.For<IClusterClient>();
        client.GetGrain<IInterClusterRelay>("local").Returns(relay);
        var provider = new DelegatingClientProvider(
            (_, _) => new ValueTask<IClusterClient>(client));
        var pending = CreateTransport(provider).SendRequest(
            new ClusterIdentity("service", "remote"),
            CreateVirtualTarget(),
            Substitute.For<IInvokable>(),
            InvokeMethodOptions.OneWay).AsTask();

        Assert.False(pending.IsCompleted);
        accepted.SetResult(Response.Completed);

        Assert.Same(Response.Completed, await pending);
        await relay.Received(1).Forward(
            new ClusterIdentity("service", "local"),
            Arg.Any<UniversalReference>(),
            Arg.Any<IInvokable>(),
            InvokeMethodOptions.OneWay,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Phase", "5")]
    public void Dispose_DisposesOwnedClientExactlyOnce()
    {
        var transportType = typeof(ClientInterClusterTransport);

        Assert.False(typeof(System.IDisposable).IsAssignableFrom(transportType));
        Assert.False(typeof(System.IAsyncDisposable).IsAssignableFrom(transportType));
    }

    [Fact]
    [Trait("Phase", "5")]
    public async Task UnavailableInterClusterTransport_EverySendFailsDeterministically()
    {
        var transport = new UnavailableInterClusterTransport();

        var first = await Assert.ThrowsAsync<System.NotSupportedException>(
            () => transport.SendRequest(
                new ClusterIdentity("service", "remote"),
                CreateVirtualTarget(),
                Substitute.For<IInvokable>(),
                InvokeMethodOptions.None).AsTask());
        var second = await Assert.ThrowsAsync<System.NotSupportedException>(
            () => transport.SendRequest(
                new ClusterIdentity("service", "remote"),
                CreateVirtualTarget(),
                Substitute.For<IInvokable>(),
                InvokeMethodOptions.OneWay).AsTask());

        Assert.Equal(first.Message, second.Message);
        Assert.Contains("'service/remote'", first.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Phase", "5")]
    public async Task UnavailableInterClusterTransport_PreCanceledToken_RemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transport = new UnavailableInterClusterTransport();

        var exception = await Assert.ThrowsAsync<System.NotSupportedException>(
            () => transport.SendRequest(
                new ClusterIdentity("service", "remote"),
                CreateVirtualTarget(),
                Substitute.For<IInvokable>(),
                InvokeMethodOptions.None,
                cancellation.Token).AsTask());

        Assert.True(cancellation.Token.IsCancellationRequested);
        Assert.Contains("not configured", exception.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Phase", "5")]
    public async Task Send_ProviderTimeout_RemainsTimeoutWhileCallerCancellationRemainsCancellation()
    {
        var timeout = new System.TimeoutException("provider timeout");
        var timeoutProvider = new DelegatingClientProvider(
            (_, _) => ValueTask.FromException<IClusterClient>(timeout));

        var actualTimeout = await Assert.ThrowsAsync<System.TimeoutException>(
            () => CreateTransport(timeoutProvider).SendRequest(
                new ClusterIdentity("service", "remote"),
                CreateVirtualTarget(),
                Substitute.For<IInvokable>(),
                InvokeMethodOptions.None).AsTask());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = Substitute.For<IInterClusterClientProvider>();
        var actualCancellation = await Assert.ThrowsAnyAsync<System.OperationCanceledException>(
            () => CreateTransport(provider).SendRequest(
                new ClusterIdentity("service", "remote"),
                CreateVirtualTarget(),
                Substitute.For<IInvokable>(),
                InvokeMethodOptions.None,
                cancellation.Token).AsTask());

        Assert.Same(timeout, actualTimeout);
        Assert.Equal(cancellation.Token, actualCancellation.CancellationToken);
        await provider.DidNotReceiveWithAnyArgs().GetClient(default, default);
    }

    [Fact]
    [Trait("Phase", "5")]
    public async Task Send_OneWayRelayFailure_PropagatesOriginalFailure()
    {
        var expected = new System.InvalidOperationException("one-way relay rejected");
        var relay = Substitute.For<IInterClusterRelay>();
        relay.Forward(
                Arg.Any<ClusterIdentity>(),
                Arg.Any<UniversalReference>(),
                Arg.Any<IInvokable>(),
                InvokeMethodOptions.OneWay,
                Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<Response>(expected));
        var client = Substitute.For<IClusterClient>();
        client.GetGrain<IInterClusterRelay>("local").Returns(relay);
        var provider = Substitute.For<IInterClusterClientProvider>();
        provider.GetClient(
                new ClusterIdentity("service", "remote"),
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IClusterClient>(client));

        var actual = await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => CreateTransport(provider).SendRequest(
                new ClusterIdentity("service", "remote"),
                CreateVirtualTarget(),
                Substitute.For<IInvokable>(),
                InvokeMethodOptions.OneWay).AsTask());

        Assert.Same(expected, actual);
        await relay.Received(1).Forward(
            new ClusterIdentity("service", "local"),
            Arg.Is<UniversalReference>(target =>
                target.Binding == UniversalReferenceBinding.Cluster
                && target.ClusterId == "remote"),
            Arg.Any<IInvokable>(),
            InvokeMethodOptions.OneWay,
            Arg.Any<CancellationToken>());
    }

    private static ClientInterClusterTransport CreateTransport(IInterClusterClientProvider provider) =>
        new(
            provider,
            Options.Create(new ClusterOptions { ServiceId = "service", ClusterId = "local" }));

    private static UniversalReference CreateVirtualTarget() =>
        UniversalReference.CreateVirtual(
            GrainId.Create("grain", "key"),
            GrainInterfaceType.Create("interface"),
            "service");

    private static UniversalReference CreateClusterTarget(string clusterId) =>
        UniversalReference.CreateCluster(
            GrainId.Create("grain", "key"),
            GrainInterfaceType.Create("interface"),
            "service",
            clusterId);

    private static ValueTask<T> AwaitCancellation<T>(
        CancellationToken cancellationToken,
        TaskCompletionSource entered)
    {
        entered.SetResult();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(
            static state =>
            {
                var (source, token) = ((TaskCompletionSource<T>, CancellationToken))state!;
                source.TrySetCanceled(token);
            },
            (completion, cancellationToken));
        return new ValueTask<T>(completion.Task);
    }

    private sealed class DelegatingClientProvider(
        System.Func<ClusterIdentity, CancellationToken, ValueTask<IClusterClient>> callback)
        : IInterClusterClientProvider
    {
        public ValueTask<IClusterClient> GetClient(
            ClusterIdentity destination,
            CancellationToken cancellationToken = default) =>
            callback(destination, cancellationToken);
    }
}

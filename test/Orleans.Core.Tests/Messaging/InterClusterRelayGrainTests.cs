using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using TestExtensions;
using Xunit;

namespace UnitTests.Messaging;

[TestArea("Messaging")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[Trait("Phase", "5")]
public sealed class InterClusterRelayGrainTests
{
    [Fact]
    public async Task Relay_ForwardsRequestAndSourceIdentityToReceiver()
    {
        var source = new ClusterIdentity("service", "source");
        var target = CreateTarget();
        var request = Substitute.For<IInvokable>();
        var receiver = Substitute.For<IInterClusterRequestReceiver>();
        receiver.ReceiveRequest(
                source,
                target,
                request,
                InvokeMethodOptions.ReadOnly,
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Response>(Response.Completed));
        var relay = new InterClusterRelayGrain(receiver);

        var actual = await relay.Forward(
            source,
            target,
            request,
            InvokeMethodOptions.ReadOnly,
            CancellationToken.None);

        Assert.Same(Response.Completed, actual);
        await receiver.Received(1).ReceiveRequest(
            source,
            target,
            request,
            InvokeMethodOptions.ReadOnly,
            CancellationToken.None);
    }

    [Fact]
    public async Task Relay_ForwardsResponseAndRemoteException()
    {
        var expectedResponse = Response.FromResult(73);
        var expectedException = new InvalidOperationException("remote failure");
        var receiver = Substitute.For<IInterClusterRequestReceiver>();
        receiver.ReceiveRequest(
                Arg.Any<ClusterIdentity>(),
                Arg.Any<UniversalReference>(),
                Arg.Any<IInvokable>(),
                Arg.Any<InvokeMethodOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ValueTask<Response>(expectedResponse),
                new ValueTask<Response>(Response.FromException(expectedException)));
        var relay = new InterClusterRelayGrain(receiver);

        var response = await relay.Forward(
            new ClusterIdentity("service", "source"),
            CreateTarget(),
            Substitute.For<IInvokable>(),
            InvokeMethodOptions.None,
            CancellationToken.None);
        var failure = await relay.Forward(
            new ClusterIdentity("service", "source"),
            CreateTarget(),
            Substitute.For<IInvokable>(),
            InvokeMethodOptions.None,
            CancellationToken.None);

        Assert.Same(expectedResponse, response);
        Assert.Equal(73, response.GetResult<int>());
        Assert.Same(expectedException, failure.Exception);
        expectedResponse.Dispose();
    }

    [Fact]
    public async Task Relay_OneWayRequest_DoesNotAwaitResponsePayload()
    {
        var receiver = Substitute.For<IInterClusterRequestReceiver>();
        receiver.ReceiveRequest(
                Arg.Any<ClusterIdentity>(),
                Arg.Any<UniversalReference>(),
                Arg.Any<IInvokable>(),
                InvokeMethodOptions.OneWay,
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Response>(Response.Completed));
        var relay = new InterClusterRelayGrain(receiver);

        var actual = await relay.Forward(
            new ClusterIdentity("service", "source"),
            CreateTarget(),
            Substitute.For<IInvokable>(),
            InvokeMethodOptions.OneWay,
            CancellationToken.None);

        Assert.Same(Response.Completed, actual);
        await receiver.Received(1).ReceiveRequest(
            Arg.Any<ClusterIdentity>(),
            Arg.Any<UniversalReference>(),
            Arg.Any<IInvokable>(),
            InvokeMethodOptions.OneWay,
            CancellationToken.None);
    }

    [Fact]
    public async Task Relay_CallerCancellation_IsPropagatedToReceiver()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new CancellationAwareReceiver(entered);
        var relay = new InterClusterRelayGrain(receiver);
        var pending = relay.Forward(
            new ClusterIdentity("service", "source"),
            CreateTarget(),
            Substitute.For<IInvokable>(),
            InvokeMethodOptions.None,
            cancellation.Token).AsTask();
        await entered.Task;

        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(cancellation.Token, receiver.ReceivedToken);
    }

    [Fact]
    public async Task Relay_CancellationTokenArgument_IsReplacedWithRelayToken()
    {
        using var originalCancellation = new CancellationTokenSource();
        using var relayCancellation = new CancellationTokenSource();
        var request = Substitute.For<IInvokable>();
        request.IsCancellable.Returns(true);
        request.GetMethod().Returns(typeof(ICancellableCall).GetMethod(nameof(ICancellableCall.Invoke))!);
        request.GetArgument(1).Returns(originalCancellation.Token);
        var receiver = new CancellationInjectingReceiver();
        var relay = new InterClusterRelayGrain(receiver);

        var actual = await relay.Forward(
            new ClusterIdentity("service", "source"),
            CreateTarget(),
            request,
            InvokeMethodOptions.None,
            relayCancellation.Token);

        Assert.Same(Response.Completed, actual);
        request.Received(1).SetArgument(1, relayCancellation.Token);
        request.DidNotReceive().SetArgument(0, Arg.Any<object>());
        Assert.Equal(relayCancellation.Token, receiver.ReceivedToken);
    }

    [Fact]
    public async Task Relay_AlreadyCanceledRequest_DoesNotInvokeReceiver()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var receiver = new CancellationAwareReceiver(
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var relay = new InterClusterRelayGrain(receiver);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => relay.Forward(
                new ClusterIdentity("service", "source"),
                CreateTarget(),
                Substitute.For<IInvokable>(),
                InvokeMethodOptions.None,
                cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, receiver.DispatchCount);
    }

    [Fact]
    public async Task Relay_ConcurrentCancellationAndCompletion_HasSingleTerminalOutcome()
    {
        using var cancellation = new CancellationTokenSource();
        var receiver = new RacingReceiver();
        var relay = new InterClusterRelayGrain(receiver);
        var pending = relay.Forward(
            new ClusterIdentity("service", "source"),
            CreateTarget(),
            Substitute.For<IInvokable>(),
            InvokeMethodOptions.None,
            cancellation.Token).AsTask();
        await receiver.Entered;
        using var barrier = new Barrier(3);
        var cancel = Task.Run(() =>
        {
            barrier.SignalAndWait(TestContext.Current.CancellationToken);
            cancellation.Cancel();
        }, TestContext.Current.CancellationToken);
        var complete = Task.Run(() =>
        {
            barrier.SignalAndWait(TestContext.Current.CancellationToken);
            receiver.Complete(Response.Completed);
        }, TestContext.Current.CancellationToken);
        barrier.SignalAndWait(TestContext.Current.CancellationToken);
        await Task.WhenAll(cancel, complete);

        try
        {
            Assert.Same(Response.Completed, await pending);
        }
        catch (OperationCanceledException exception)
        {
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }

        Assert.Equal(1, receiver.TerminalOutcomeCount);
        Assert.True(pending.IsCompleted);
    }

    private static UniversalReference CreateTarget() =>
        UniversalReference.CreateCluster(
            GrainId.Create("grain", "relay-key"),
            GrainInterfaceType.Create("interface"),
            "service",
            "destination");

    private interface ICancellableCall
    {
        Task Invoke(int value, CancellationToken cancellationToken);
    }

    private sealed class CancellationInjectingReceiver : IInterClusterRequestReceiver
    {
        public CancellationToken ReceivedToken { get; private set; }

        public ValueTask<Response> ReceiveRequest(
            ClusterIdentity source,
            UniversalReference target,
            IInvokable request,
            InvokeMethodOptions options,
            CancellationToken cancellationToken = default)
        {
            ReceivedToken = cancellationToken;
            var parameters = request.GetMethod().GetParameters();
            for (var index = 0; index < parameters.Length; index++)
            {
                if (parameters[index].ParameterType == typeof(CancellationToken))
                {
                    request.SetArgument(index, cancellationToken);
                    break;
                }
            }

            return new ValueTask<Response>(Response.Completed);
        }
    }

    private sealed class CancellationAwareReceiver(TaskCompletionSource entered)
        : IInterClusterRequestReceiver
    {
        public CancellationToken ReceivedToken { get; private set; }

        public int DispatchCount { get; private set; }

        public ValueTask<Response> ReceiveRequest(
            ClusterIdentity source,
            UniversalReference target,
            IInvokable request,
            InvokeMethodOptions options,
            CancellationToken cancellationToken = default)
        {
            ReceivedToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            DispatchCount++;
            entered.TrySetResult();
            var completion = new TaskCompletionSource<Response>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(
                static state =>
                {
                    var (source, token) =
                        ((TaskCompletionSource<Response>, CancellationToken))state!;
                    source.TrySetCanceled(token);
                },
                (completion, cancellationToken));
            return new ValueTask<Response>(completion.Task);
        }
    }

    private sealed class RacingReceiver : IInterClusterRequestReceiver
    {
        private readonly TaskCompletionSource<Response> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _terminalOutcomeCount;

        public Task Entered { get; private set; } = Task.CompletedTask;

        public int TerminalOutcomeCount => _terminalOutcomeCount;

        public ValueTask<Response> ReceiveRequest(
            ClusterIdentity source,
            UniversalReference target,
            IInvokable request,
            InvokeMethodOptions options,
            CancellationToken cancellationToken = default)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Entered = entered.Task;
            cancellationToken.Register(
                static state =>
                {
                    var (receiver, token) = ((RacingReceiver, CancellationToken))state!;
                    if (receiver._completion.TrySetCanceled(token))
                    {
                        Interlocked.Increment(ref receiver._terminalOutcomeCount);
                    }
                },
                (this, cancellationToken));
            entered.SetResult();
            return new ValueTask<Response>(_completion.Task);
        }

        public void Complete(Response response)
        {
            if (_completion.TrySetResult(response))
            {
                Interlocked.Increment(ref _terminalOutcomeCount);
            }
        }
    }
}

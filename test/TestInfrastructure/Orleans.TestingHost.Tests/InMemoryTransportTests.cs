using System.Net;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Connections.Transport;
using Orleans.Serialization.Buffers;
using Orleans.TestingHost.InMemoryTransport;
using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT")]
public class InMemoryTransportTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task ConcurrentShutdownWithoutCallbacksClosesRetainedWaitHandle()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        // Do not bind: hub registration would add a cancellation callback and mask the no-callback regression.
        await using var listener = new InMemoryTransportListener(
            "test", new IPEndPoint(IPAddress.Loopback, 12349).ToString(), new InMemoryTransportConnectionHub());
        var token = listener.OnDisposed;
        var waitHandle = token.WaitHandle;
        Assert.True(listener.IsValid);
        Assert.False(token.IsCancellationRequested);
        Assert.False(waitHandle.WaitOne(0));
        var startShutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unbind = Shutdown(dispose: false);
        var disposal = Shutdown(dispose: true);

        startShutdown.SetResult();
        await Task.WhenAll(unbind, disposal).WaitAsync(timeout.Token);

        Assert.False(listener.IsValid);
        Assert.Equal(token, listener.OnDisposed);
        Assert.True(token.IsCancellationRequested);
        Assert.True(listener.OnDisposed.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
        Assert.Throws<ObjectDisposedException>(() => waitHandle.WaitOne(0));
        var repeatedUnbind = listener.UnbindAsync(timeout.Token).AsTask();
        var repeatedDisposal = listener.DisposeAsync().AsTask();
        Assert.Same(repeatedUnbind, repeatedDisposal);
        await Task.WhenAll(repeatedUnbind, repeatedDisposal).WaitAsync(timeout.Token);

        async Task Shutdown(bool dispose)
        {
            await startShutdown.Task;
            if (dispose)
            {
                await listener.DisposeAsync();
            }
            else
            {
                await listener.UnbindAsync(timeout.Token);
            }
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task ConcurrentUnbindWaitsForNotificationsBeforeReleasingCancellationResources()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var hub = new InMemoryTransportConnectionHub();
        var endpoint = new IPEndPoint(IPAddress.Loopback, 12348);
        await using var listener = new InMemoryTransportListener("test", endpoint.ToString(), hub);
        await listener.BindAsync(timeout.Token);
        var token = listener.OnDisposed;
        var waitHandle = token.WaitHandle;
        var notifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() =>
        {
            notifying.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        });
        var first = Task.Run(async () => await listener.UnbindAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        try
        {
            await notifying.Task.WaitAsync(timeout.Token);
            var second = listener.DisposeAsync().AsTask();
            Assert.True(token.IsCancellationRequested);
            Assert.Same(waitHandle, token.WaitHandle);
            Assert.True(waitHandle.WaitOne(0));
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            release.SetResult();
            await Task.WhenAll(first, second).WaitAsync(timeout.Token);

            Assert.False(listener.IsValid);
            Assert.True(listener.OnDisposed.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
            Assert.Throws<ObjectDisposedException>(() => waitHandle.WaitOne(0));
            await listener.UnbindAsync(timeout.Token);
        }
        finally
        {
            release.TrySetResult();
            await first;
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact]
    public async Task UnbindReleasesCancellationResourcesWhenNotificationFails()
    {
        var listener = new InMemoryTransportListener("test", endpointValue: null, new InMemoryTransportConnectionHub());
        var token = listener.OnDisposed;
        var waitHandle = token.WaitHandle;
        var expected = new InvalidOperationException("Listener notification failed.");
        using var registration = token.Register(() => throw expected);
        try
        {
            var unbind = listener.UnbindAsync(TestContext.Current.CancellationToken).AsTask();
            var disposal = listener.DisposeAsync().AsTask();
            Assert.Same(unbind, disposal);
            var failure = await Assert.ThrowsAsync<AggregateException>(
                () => unbind);

            Assert.Same(expected, Assert.Single(failure.InnerExceptions));
            Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => disposal));
            Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(
                () => listener.UnbindAsync(TestContext.Current.CancellationToken).AsTask()));
            Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => listener.DisposeAsync().AsTask()));
            Assert.False(listener.IsValid);
            Assert.True(listener.OnDisposed.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
            Assert.Throws<ObjectDisposedException>(() => waitHandle.WaitOne(0));
        }
        finally
        {
            await Assert.ThrowsAsync<AggregateException>(() => listener.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task PreCanceledBindLeavesEndpointUnregistered()
    {
        var hub = new InMemoryTransportConnectionHub();
        var endpoint = new IPEndPoint(IPAddress.Loopback, 12347);
        await using var listener = new InMemoryTransportListener("test", endpoint.ToString(), hub);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => listener.BindAsync(cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, error.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => hub.GetConnectionListenerFactoryAsync(endpoint.ToString(), cancellation.Token).AsTask());
        await listener.BindAsync(TestContext.Current.CancellationToken);
        Assert.Same(listener, await hub.GetConnectionListenerFactoryAsync(endpoint.ToString(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TestClusterOptions_DefaultConnectionTransport_IsInMemory()
    {
        Assert.Equal(ConnectionTransportType.InMemory, new TestClusterOptions().ConnectionTransport);
    }

    [Fact]
    public async Task ConnectorWaitsForListenerRegistration()
    {
        var hub = new InMemoryTransportConnectionHub();
        var endpoint = new IPEndPoint(IPAddress.Loopback, 12345);
        var connector = new InMemoryTransportConnector(hub, NullLoggerFactory.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var connectTask = connector.CreateAsync(endpoint, timeout.Token).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        Assert.False(connectTask.IsCompleted);

        var listener = new InMemoryTransportListener("test", endpoint.ToString(), hub);
        await listener.BindAsync(timeout.Token);
        var acceptTask = listener.AcceptAsync(timeout.Token).AsTask();

        await using var outbound = await connectTask;
        await using var inbound = Assert.IsType<InMemoryMessageTransport>(await acceptTask);

        await listener.DisposeAsync();
    }

    [Fact]
    public async Task CanceledConnectIsNotAccepted()
    {
        var hub = new InMemoryTransportConnectionHub();
        var endpoint = new IPEndPoint(IPAddress.Loopback, 12346);
        var connector = new InMemoryTransportConnector(hub, NullLoggerFactory.Instance);
        await using var listener = new InMemoryTransportListener("test", endpoint.ToString(), hub);
        await listener.BindAsync(TestContext.Current.CancellationToken);
        using var connectCancellation = new CancellationTokenSource();

        var connectTask = connector.CreateAsync(endpoint, connectCancellation.Token).AsTask();
        connectCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connectTask);

        using var acceptCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.AcceptAsync(acceptCancellation.Token).AsTask());
    }

    [Fact]
    public async Task CompletedPipeDeliversFinalBuffer()
    {
        var input = new Pipe();
        var unusedOutput = new Pipe();
        await input.Writer.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
        await input.Writer.CompleteAsync();
        await using var transport = new InMemoryMessageTransport(
            new TestDuplexPipe(input.Reader, unusedOutput.Writer),
            NullLogger.Instance);
        transport.Start();
        var request = new TestReadRequest(3);

        Assert.True(transport.EnqueueRead(request));

        Assert.Equal(
            [1, 2, 3],
            await request.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompletedPeerFailsActiveAndQueuedWrites()
    {
        var input = new Pipe();
        var output = new Pipe();
        await output.Reader.CompleteAsync();
        await using var transport = new InMemoryMessageTransport(
            new TestDuplexPipe(input.Reader, output.Writer),
            NullLogger.Instance);
        using var active = new TestWriteRequest([1]);
        using var queued = new TestWriteRequest([2]);
        Assert.True(transport.EnqueueWrite(active));
        Assert.True(transport.EnqueueWrite(queued));
        transport.Start();

        await Assert.ThrowsAsync<ConnectionClosedException>(
            () => active.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ConnectionClosedException>(
            () => queued.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(1, active.CompletionCount);
        Assert.Equal(1, queued.CompletionCount);
    }

    [Fact]
    public async Task ListenerWithoutEndpoint_IsDisabledAndNotRegistered()
    {
        var hub = new InMemoryTransportConnectionHub();
        await using var listener = new InMemoryTransportListener("gateway", endpointValue: null, hub);

        Assert.False(listener.IsValid);
        await listener.BindAsync(TestContext.Current.CancellationToken);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => hub.GetConnectionListenerFactoryAsync("missing", cancellation.Token).AsTask());
    }

    private sealed class TestDuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }

    private sealed class TestReadRequest(int length) : ReadRequest
    {
        private readonly TaskCompletionSource<byte[]> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<byte[]> Completion => _completion.Task;

        public override bool OnRead(ArcBufferReader buffer)
        {
            if (buffer.Length < length)
            {
                return false;
            }

            var result = new byte[length];
            buffer.Consume(result);
            _completion.TrySetResult(result);
            return true;
        }

        public override void OnError(Exception error) => _completion.TrySetException(error);
        public override void OnCanceled() => _completion.TrySetCanceled();
    }

    private sealed class TestWriteRequest : WriteRequest, IDisposable
    {
        private readonly ArcBufferWriter _buffer = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completionCount;

        public TestWriteRequest(ReadOnlySpan<byte> bytes)
        {
            _buffer.Write(bytes);
            Buffers = _buffer.Reader;
        }

        public Task Completion => _completion.Task;
        public int CompletionCount => Volatile.Read(ref _completionCount);

        public override void SetResult()
        {
            Interlocked.Increment(ref _completionCount);
            _completion.SetResult();
        }

        public override void SetException(Exception error)
        {
            Interlocked.Increment(ref _completionCount);
            _completion.SetException(error);
        }

        public void Dispose() => _buffer.Dispose();
    }

}

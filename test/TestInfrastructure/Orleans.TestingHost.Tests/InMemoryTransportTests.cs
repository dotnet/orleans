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

}

using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Connections.Transport;
using Orleans.Connections.Transport.Sockets;
using Orleans.Serialization.Buffers;
using TestExtensions;
using Xunit;

namespace Orleans.Core.Tests.Networking;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class SocketMessageTransportWriteTests
{
    [Theory]
    [InlineData(31, false)]
    [InlineData(32, false)]
    [InlineData(33, false)]
    [InlineData(63, true)]
    [InlineData(64, true)]
    [InlineData(65, true)]
    public async Task SingleWrite_CompletesAtBufferLimitWithoutAnotherRequest(int segments, bool largeMessages)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var accept = listener.AcceptAsync(cancellation.Token).AsTask();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.LocalEndPoint!, cancellation.Token);
        using var peer = await accept;
        await using var transport = new SocketMessageTransport(client, NullLogger.Instance);
        var expected = Enumerable.Range(0, segments * ArcBufferWriter.MinimumPageSize)
            .Select(static index => (byte)(index % 251)).ToArray();
        using var request = new SegmentedWriteRequest(expected, largeMessages);
        using (var slice = request.Buffers.PeekSlice(expected.Length))
        {
            var enumerator = slice.ArraySegments;
            for (var i = 0; i < segments; i++)
            {
                Assert.True(enumerator.MoveNext());
                Assert.Equal(ArcBufferWriter.MinimumPageSize, enumerator.Current.Count);
                Assert.Equal(i == segments - 1, enumerator.IsCompleted);
            }
        }

        transport.Start();
        Assert.True(transport.EnqueueWrite(request));
        var actual = new byte[expected.Length];
        var received = 0;
        while (received < actual.Length)
        {
            var count = await peer.ReceiveAsync(actual.AsMemory(received), SocketFlags.None, cancellation.Token);
            Assert.NotEqual(0, count);
            received += count;
        }

        await request.Completion.WaitAsync(cancellation.Token);

        Assert.Equal(expected, actual);
        Assert.Equal(0, request.Buffers.Length);
        Assert.False(transport.Closed.IsCancellationRequested);
    }

    private sealed class SegmentedWriteRequest : WriteRequest, IDisposable
    {
        private readonly ArcBufferWriter _buffer = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SegmentedWriteRequest(byte[] bytes, bool largeMessages)
        {
            _buffer.Write(bytes);
            Buffers = new(_buffer);
            HasLargeMessages = largeMessages;
        }

        public Task Completion => _completion.Task;
        internal override bool HasLargeMessages { get; }

        public override void SetResult() => _completion.SetResult();
        public override void SetException(Exception error) => _completion.SetException(error);
        public void Dispose() => _buffer.Dispose();
    }
}

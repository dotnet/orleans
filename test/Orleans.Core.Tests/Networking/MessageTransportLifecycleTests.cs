#nullable enable
using System;
using System.Buffers;
using System.Linq;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Connections;
using Orleans.Connections.Transport;
using Orleans.Connections.Transport.Streams;
using Orleans.Connections.Transport.Sockets;
using Orleans.Connections.Transport.Security;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Core.Tests.Networking;

public class MessageTransportLifecycleTests
{
    [Fact]
    public void ConnectionOptions_CloseConnectionTimeout_HasCorrectDefault()
    {
        var options = new ConnectionOptions();
        Assert.Equal(TimeSpan.FromSeconds(30), options.CloseConnectionTimeout);
    }

    [Fact]
    public void ConnectionOptions_CloseConnectionTimeout_CanBeModified()
    {
        var options = new ConnectionOptions();
        var customTimeout = TimeSpan.FromSeconds(60);

        options.CloseConnectionTimeout = customTimeout;

        Assert.Equal(customTimeout, options.CloseConnectionTimeout);
    }

    [Fact]
    public void ConnectionOptions_CloseConnectionTimeout_CanBeSetToShortValue()
    {
        var options = new ConnectionOptions();
        var shortTimeout = TimeSpan.FromMilliseconds(100);

        options.CloseConnectionTimeout = shortTimeout;

        Assert.Equal(shortTimeout, options.CloseConnectionTimeout);
    }

    [Fact]
    public void ConnectionClosedException_HasProperMessage()
    {
        var message = "Test close reason";
        var exception = new ConnectionClosedException(message);

        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public void ConnectionClosedException_PreservesInnerException()
    {
        var innerException = new InvalidOperationException("Inner error");
        var exception = new ConnectionClosedException("Outer", innerException);

        Assert.Equal(innerException, exception.InnerException);
    }

    [Fact]
    public void ConnectionAbortedException_HasProperMessage()
    {
        var message = "Test abort reason";
        var exception = new ConnectionAbortedException(message);

        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public void ConnectionAbortedException_PreservesInnerException()
    {
        var innerException = new InvalidOperationException("Inner error");
        var exception = new ConnectionAbortedException("Outer", innerException);

        Assert.Equal(innerException, exception.InnerException);
    }

    [Fact]
    public void ConnectionOptions_DEFAULT_CLOSECONNECTION_TIMEOUT_HasCorrectValue()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ConnectionOptions.DEFAULT_CLOSECONNECTION_TIMEOUT);
    }

    [Fact]
    public void MessageSerializer_Write_PreservesBufferedRawResponseForRetry()
    {
        using var serviceProvider = CreateServiceProvider();
        var sessionPool = serviceProvider.GetRequiredService<SerializerSessionPool>();
        var serializer = new MessageSerializer(sessionPool, new SiloMessagingOptions());
        var shared = CreateMessageHandlerShared(serviceProvider);
        using var bodyWriter = new ArcBufferWriter();
        byte[] bodyBytes = [1, 2, 3, 4];
        bodyWriter.Write(bodyBytes);

        var readRequest = new MessageReadRequest(shared);
        readRequest._originalResponseType = Message.ResponseTypes.Success;
        readRequest.Body = bodyWriter.ConsumeSlice(bodyBytes.Length);
        typeof(MessageReadRequest).GetField("_bodyLength", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(readRequest, bodyBytes.Length);

        var message = new Message
        {
            Direction = Message.Directions.Response,
            Result = Message.ResponseTypes.Success,
            BodyObject = readRequest
        };

        using var firstOutput = new ArcBufferWriter();
        using var secondOutput = new ArcBufferWriter();
        var firstLengths = serializer.Write(firstOutput, message);
        var secondLengths = serializer.Write(secondOutput, message);

        Assert.Equal(bodyBytes.Length, firstLengths.BodyLength);
        Assert.Equal(firstLengths, secondLengths);
        var firstBytes = new byte[firstOutput.Length];
        var secondBytes = new byte[secondOutput.Length];
        firstOutput.Peek(firstBytes);
        secondOutput.Peek(secondBytes);
        Assert.Equal(firstBytes, secondBytes);
        Assert.Equal(bodyBytes, firstBytes[firstLengths.HeaderLength..(firstLengths.HeaderLength + firstLengths.BodyLength)]);
        Assert.Same(readRequest, message._bodyObject);
        message.Dispose();
    }

    [Fact]
    public void MessageSerializer_ReadCacheInvalidationHeaders_ConsumesAllEntriesAndRetainsLimit()
    {
        using var serviceProvider = CreateServiceProvider();
        var serializer = new MessageSerializer(serviceProvider.GetRequiredService<SerializerSessionPool>(), new SiloMessagingOptions());
        var entries = Enumerable.Range(0, Message.MaxCacheInvalidationHeaderEntries + 2)
            .Select(static i =>
            {
                var grainId = GrainId.Create("test", i.ToString());
                return new GrainAddressCacheUpdate(
                    new GrainAddress
                    {
                        GrainId = grainId,
                        ActivationId = ActivationId.NewId(),
                        SiloAddress = SiloAddress.New(IPAddress.Loopback, 10_000 + i, i + 1)
                    },
                    validAddress: null);
            })
            .ToList();
        var message = new Message { CacheInvalidationHeader = entries };
        using var buffer = new ArcBufferWriter();
        var (headerLength, _) = serializer.Write(buffer, message);
        var shared = CreateMessageHandlerShared(serviceProvider);
        var readRequest = new MessageReadRequest(shared);
        readRequest.Headers = buffer.ConsumeSlice(headerLength);
        typeof(MessageReadRequest).GetField("_headerLength", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(readRequest, headerLength);

        serializer.ReadHeaders(readRequest, out var deserialized);

        var invalidationHeader = Assert.IsType<List<GrainAddressCacheUpdate>>(deserialized.CacheInvalidationHeader);
        Assert.Equal(Message.MaxCacheInvalidationHeaderEntries, invalidationHeader.Count);
        Assert.Equal(entries.Take(Message.MaxCacheInvalidationHeaderEntries).Select(static entry => entry.GrainId),
            invalidationHeader.Select(static entry => entry.GrainId));
        readRequest.Reset();
    }

    [Fact]
    public void MessageFactory_ResponseTimeToLive_IsSerialized()
    {
        using var serviceProvider = CreateServiceProvider();
        var serializer = new MessageSerializer(serviceProvider.GetRequiredService<SerializerSessionPool>(), new SiloMessagingOptions());
        var shared = CreateMessageHandlerShared(serviceProvider);
        var factory = new MessageFactory(
            serviceProvider.GetRequiredService<DeepCopier>(),
            NullLogger<MessageFactory>.Instance,
            shared.MessagingTrace);
        var request = new Message { TimeToLive = TimeSpan.FromMinutes(1) };
        var response = factory.CreateResponseMessage(request);
        using var buffer = new ArcBufferWriter();
        var (headerLength, _) = serializer.Write(buffer, response);
        var readRequest = new MessageReadRequest(shared);
        readRequest.Headers = buffer.ConsumeSlice(headerLength);
        typeof(MessageReadRequest).GetField("_headerLength", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(readRequest, headerLength);

        serializer.ReadHeaders(readRequest, out var deserialized);

        Assert.NotNull(deserialized.TimeToLive);
        readRequest.Reset();
    }

    [Fact]
    public void Message_DeserializeRequestBodyFailure_ReturnsReadRequestOnce()
    {
        using var serviceProvider = CreateServiceProvider();
        var shared = CreateMessageHandlerShared(serviceProvider);
        using var bodyWriter = new ArcBufferWriter();
        bodyWriter.Write([0xff]);
        var readRequest = new MessageReadRequest(shared);
        readRequest.Body = bodyWriter.ConsumeSlice(1);
        typeof(MessageReadRequest).GetField("_bodyLength", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(readRequest, 1);
        var message = new Message { Direction = Message.Directions.Request };
        message.SetMessageReadRequest(readRequest);

        Assert.ThrowsAny<Exception>(() => _ = message.BodyObject);
        Assert.Null(message._bodyObject);
        message.Dispose();

        var first = shared.GetReceiveMessageHandler();
        var second = shared.GetReceiveMessageHandler();
        Assert.Same(readRequest, first);
        Assert.NotSame(first, second);
        first.Reset();
        second.Reset();
    }

    [Fact]
    public void Message_Dispose_ReleasesLazyBodyBuffer()
    {
        using var serviceProvider = CreateServiceProvider();
        var shared = CreateMessageHandlerShared(serviceProvider);
        using var bodyWriter = new ArcBufferWriter();
        bodyWriter.Write([1, 2, 3]);
        var readRequest = new MessageReadRequest(shared);
        readRequest.Body = bodyWriter.ConsumeSlice(3);
        typeof(MessageReadRequest).GetField("_bodyLength", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(readRequest, 3);
        var message = new Message();
        message.SetMessageReadRequest(readRequest);

        message.Dispose();

        Assert.Null(message._bodyObject);
        Assert.Equal(0, readRequest.BodyLength);
        Assert.Equal(0, readRequest.Body.Length);
    }

    [Fact]
    public void MessageHandlerShared_DoesNotReuseSerializersAcrossInstances()
    {
        using var firstServiceProvider = CreateServiceProvider();
        using var secondServiceProvider = CreateServiceProvider();
        var first = CreateMessageHandlerShared(firstServiceProvider);
        var second = CreateMessageHandlerShared(secondServiceProvider);

        var serializer = first.GetMessageSerializer();
        first.Return(serializer);
        var other = second.GetMessageSerializer();

        Assert.NotSame(serializer, other);
        second.Return(other);
    }

    [Fact]
    public void MessageHandlerShared_DoesNotReuseHandlersAcrossInstances()
    {
        using var firstServiceProvider = CreateServiceProvider();
        using var secondServiceProvider = CreateServiceProvider();
        var first = CreateMessageHandlerShared(firstServiceProvider);
        var second = CreateMessageHandlerShared(secondServiceProvider);

        var readHandler = first.GetReceiveMessageHandler();
        first.Return(readHandler);
        var otherReadHandler = second.GetReceiveMessageHandler();

        Assert.NotSame(readHandler, otherReadHandler);
        second.Return(otherReadHandler);

        var writeHandler = first.GetSendMessageHandler();
        first.Return(writeHandler);
        var otherWriteHandler = second.GetSendMessageHandler();

        Assert.NotSame(writeHandler, otherWriteHandler);
        second.Return(otherWriteHandler);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    public void MessageSerializer_ValidateFrameLengths_RejectsMalformedLengths(int headerLength, int bodyLength)
    {
        using var serviceProvider = CreateServiceProvider();
        var serializer = new MessageSerializer(serviceProvider.GetRequiredService<SerializerSessionPool>(), new SiloMessagingOptions());

        Assert.Throws<InvalidMessageFrameException>(() => serializer.ValidateFrameLengths(headerLength, bodyLength));
    }

    [Fact]
    public void MessageSerializer_ValidateFrameLengths_RejectsOverflow()
    {
        using var serviceProvider = CreateServiceProvider();
        var serializer = new MessageSerializer(
            serviceProvider.GetRequiredService<SerializerSessionPool>(),
            new SiloMessagingOptions { MaxMessageHeaderSize = int.MaxValue, MaxMessageBodySize = int.MaxValue });

        Assert.Throws<InvalidMessageFrameException>(() => serializer.ValidateFrameLengths(int.MaxValue, int.MaxValue));
    }

    [Fact]
    public void MessageWriteRequest_SerializationFailure_PreservesValidPrefix()
    {
        using var serviceProvider = CreateServiceProvider(new SiloMessagingOptions { MaxMessageBodySize = 1 });
        var shared = CreateMessageHandlerShared(serviceProvider);
        var request = new MessageWriteRequest(shared);
        var valid = new Message();
        var invalid = new Message { BodyObject = new byte[2] };

        request.WriteMessage(valid);
        var validLength = request.Length;

        Assert.Throws<InvalidMessageFrameException>(() => request.WriteMessage(invalid));
        Assert.Equal(validLength, request.Length);
        Assert.Equal(1, request.MessageCount);
        Assert.Same(valid, request.GetMessage(0));
        request.Reset();
    }

    [Fact]
    public void MessageWriteRequest_LargeMessageState_TracksFramesAndAdaptsPageSize()
    {
        using var serviceProvider = CreateServiceProvider();
        var shared = CreateMessageHandlerShared(serviceProvider);
        var request = shared.GetSendMessageHandler();

        request.WriteMessage(new Message());
        Assert.False(request.HasLargeMessages);
        request.Reset();

        request = shared.GetSendMessageHandler();
        request.WriteMessage(new Message { BodyObject = new byte[8 * 1024] });
        Assert.True(request.HasLargeMessages);

        request.Reset();
        var reused = shared.GetSendMessageHandler();
        Assert.Same(request, reused);
        Assert.False(reused.HasLargeMessages);

        reused.WriteMessage(new Message { BodyObject = new byte[16 * 1024] });
        var segmentCount = 0;
        using var slice = reused.Buffers.ConsumeSlice(reused.Buffers.Length);
        var segments = slice.ArraySegments;
        while (segments.MoveNext())
        {
            segmentCount++;
        }

        Assert.Equal(1, segmentCount);
        reused.Reset();
    }

    [Fact]
    public async Task MessageTransportStream_ReadCancellation_ClosesInnerTransport()
    {
        var transport = new CancelableTransport();
        await using var stream = new MessageTransportStream(transport, MemoryPool<byte>.Shared);
        using var cancellation = new CancellationTokenSource();

        var read = stream.ReadAsync(new byte[1], cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.True(transport.CloseCalled);
    }

    [Fact]
    public async Task MessageTransportStream_WriteCancellation_ClosesInnerTransport()
    {
        var transport = new CancelableTransport();
        await using var stream = new MessageTransportStream(transport, MemoryPool<byte>.Shared);
        using var cancellation = new CancellationTokenSource();

        var write = stream.WriteAsync(new byte[1], cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.True(transport.CloseCalled);
    }

    [Fact]
    public void MessageTransportStream_SynchronousWrite_UsesRequestedLength()
    {
        var transport = new CapturingTransport();
        using var stream = new MessageTransportStream(transport, MemoryPool<byte>.Shared);
        byte[] bytes = [1, 2, 3];

        stream.Write(bytes);

        Assert.Equal(bytes, transport.Written);
    }

    [Fact]
    public void MessageTransportStream_SynchronousRead_ReturnsTransportBytes()
    {
        byte[] bytes = [1, 2, 3];
        var transport = new ImmediateReadTransport(bytes);
        using var stream = new MessageTransportStream(transport, MemoryPool<byte>.Shared);
        Span<byte> destination = stackalloc byte[bytes.Length];

        var bytesRead = stream.Read(destination);

        Assert.Equal(bytes.Length, bytesRead);
        Assert.Equal(bytes, destination.ToArray());
    }

    [Fact]
    public async Task SocketMessageTransport_ZeroLengthWrite_Completes()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var clientSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        var connect = clientSocket.ConnectAsync(listener.LocalEndpoint, TestContext.Current.CancellationToken);
        using var serverSocket = await listener.AcceptSocketAsync(TestContext.Current.CancellationToken);
        await connect;
        await using var transport = new SocketMessageTransport(clientSocket, NullLogger.Instance);
        transport.Start();
        using var request = new EmptyWriteRequest();

        Assert.True(transport.EnqueueWrite(request));
        await request.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await transport.CloseAsync(null, TestContext.Current.CancellationToken);
        listener.Stop();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TcpMessageTransportConnector_AppliesDualModeToIpv6Socket(bool dualMode)
    {
        if (!Socket.OSSupportsIPv6)
        {
            throw Xunit.Sdk.SkipException.ForSkip("IPv6 is not supported.");
        }

        var options = Substitute.For<IOptionsMonitor<TcpMessageTransportOptions>>();
        options.CurrentValue.Returns(new TcpMessageTransportOptions
        {
            DualMode = dualMode,
            FastPath = false
        });
        var connector = new TcpMessageTransportConnector(options, NullLoggerFactory.Instance);
        var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Server.DualMode = false;
        listener.Start();
        try
        {
            var connectTask = connector.CreateAsync(
                listener.LocalEndpoint,
                TestContext.Current.CancellationToken).AsTask();
            using var acceptedSocket = await listener.AcceptSocketAsync(TestContext.Current.CancellationToken);
            await using var transport = await connectTask;
            var socket = (Socket)typeof(SocketMessageTransport)
                .GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(transport)!;

            Assert.Equal(dualMode, socket.DualMode);

            await transport.CloseAsync(null, TestContext.Current.CancellationToken);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task StreamMessageTransport_WriteFailure_WakesIdleReadLoop()
    {
        await using var transport = new TestStreamMessageTransport(new FailingWriteStream());
        transport.Start();
        using var request = new BufferedWriteRequest([1]);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = transport.Closed.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), closed);

        Assert.True(transport.EnqueueWrite(request));
        await Assert.ThrowsAsync<IOException>(() => request.Completion);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TlsConnector_ConstructionFailure_DisposesInnerTransport()
    {
        var inner = new TrackingTransport();
        var options = Substitute.For<IOptionsMonitor<TlsOptions>>();
        options.CurrentValue.Returns(new TlsOptions { ClientCertificateMode = RemoteCertificateMode.RequireCertificate });
        await using var connector = new TlsMessageTransportConnector(new TestConnector(inner), options, NullLoggerFactory.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connector.CreateAsync(
                new IPEndPoint(IPAddress.Loopback, 1),
                TestContext.Current.CancellationToken).AsTask());

        Assert.True(inner.Disposed);
    }

    [Fact]
    public async Task TlsListener_ConstructionFailure_DisposesConnectionAndContinuesAccepting()
    {
        var inner = new TrackingTransport();
        var options = Substitute.For<IOptionsMonitor<TlsOptions>>();
        options.Get(Arg.Any<string>()).Returns(new TlsOptions());
        await using var listener = new TlsMessageTransportListener(new TestListener(inner), options, NullLoggerFactory.Instance);

        Assert.Null(await listener.AcceptAsync(TestContext.Current.CancellationToken));
        Assert.True(inner.Disposed);
    }

    private static ServiceProvider CreateServiceProvider(SiloMessagingOptions? options = null) => new ServiceCollection()
        .AddMetrics()
        .AddSerializer()
        .AddSingleton<OrleansInstruments>()
        .AddSingleton<MessagingInstruments>()
        .AddSingleton<MessagingProcessingInstruments>()
        .AddTransient(sp => new MessageSerializer(sp.GetRequiredService<SerializerSessionPool>(), options ?? new SiloMessagingOptions()))
        .BuildServiceProvider();

    private static MessageHandlerShared CreateMessageHandlerShared(IServiceProvider serviceProvider)
    {
        var messagingInstruments = serviceProvider.GetRequiredService<MessagingInstruments>();
        var messagingTrace = new MessagingTrace(
            NullLoggerFactory.Instance,
            messagingInstruments,
            serviceProvider.GetRequiredService<MessagingProcessingInstruments>());
        return new(
            messagingTrace,
            new ConnectionTrace(NullLoggerFactory.Instance),
            serviceProvider,
            new MessageFactory(serviceProvider.GetRequiredService<DeepCopier>(), NullLogger<MessageFactory>.Instance, messagingTrace),
            Substitute.For<IMessageCenter>(),
            messagingInstruments);
    }

    private sealed class CancelableTransport : MessageTransport
    {
        private ReadRequest? _read;
        private WriteRequest? _write;

        public bool CloseCalled { get; private set; }
        public override CancellationToken Closed => default;
        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override bool EnqueueRead(ReadRequest request)
        {
            _read = request;
            return true;
        }

        public override bool EnqueueWrite(WriteRequest request)
        {
            _write = request;
            return true;
        }

        public override ValueTask CloseAsync(Exception? closeException, CancellationToken cancellationToken = default)
        {
            CloseCalled = true;
            var error = closeException ?? new ConnectionClosedException();
            _read?.OnCanceled();
            _write?.SetException(error);
            return default;
        }
    }

    private sealed class CapturingTransport : MessageTransport
    {
        public byte[]? Written { get; private set; }
        public override CancellationToken Closed => default;
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override bool EnqueueRead(ReadRequest request) => false;

        public override bool EnqueueWrite(WriteRequest request)
        {
            Written = new byte[request.Buffers.Length];
            request.Buffers.Consume(Written);
            request.SetResult();
            return true;
        }

        public override ValueTask CloseAsync(Exception? closeException, CancellationToken cancellationToken = default) => default;
    }

    private sealed class ImmediateReadTransport : MessageTransport
    {
        private readonly ArcBufferWriter _buffer = new();

        public ImmediateReadTransport(ReadOnlySpan<byte> bytes) => _buffer.Write(bytes);

        public override CancellationToken Closed => default;
        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override bool EnqueueRead(ReadRequest request)
        {
            request.OnRead(new ArcBufferReader(_buffer));
            return true;
        }

        public override bool EnqueueWrite(WriteRequest request) => false;

        public override ValueTask CloseAsync(Exception? closeException, CancellationToken cancellationToken = default) => default;

        public override ValueTask DisposeAsync()
        {
            _buffer.Dispose();
            return default;
        }
    }

    private sealed class EmptyWriteRequest : WriteRequest, IDisposable
    {
        private readonly ArcBufferWriter _buffer = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EmptyWriteRequest() => Buffers = new(_buffer);
        public Task Completion => _completion.Task;
        public override void SetResult() => _completion.TrySetResult();
        public override void SetException(Exception error) => _completion.TrySetException(error);
        public void Dispose() => _buffer.Dispose();
    }

    private sealed class BufferedWriteRequest : WriteRequest, IDisposable
    {
        private readonly ArcBufferWriter _buffer = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BufferedWriteRequest(ReadOnlySpan<byte> bytes)
        {
            _buffer.Write(bytes);
            Buffers = new(_buffer);
        }

        public Task Completion => _completion.Task;
        public override void SetResult() => _completion.TrySetResult();
        public override void SetException(Exception error) => _completion.TrySetException(error);
        public void Dispose() => _buffer.Dispose();
    }

    private sealed class TestStreamMessageTransport(Stream stream) : StreamMessageTransport(NullLogger.Instance)
    {
        protected override Stream Stream { get; } = stream;
    }

    private sealed class FailingWriteStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Write failed"));
    }

    private sealed class TrackingTransport : MessageTransport
    {
        public bool Disposed { get; private set; }
        public override CancellationToken Closed => default;
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override bool EnqueueRead(ReadRequest request) => false;
        public override bool EnqueueWrite(WriteRequest request) => false;
        public override ValueTask CloseAsync(Exception? closeException, CancellationToken cancellationToken = default) => default;

        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return default;
        }
    }

    private sealed class TestConnector(MessageTransport transport) : MessageTransportConnector
    {
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override bool IsValid => true;
        public override ValueTask<MessageTransport> CreateAsync(EndPoint endPoint, CancellationToken cancellationToken = default) => new(transport);
    }

    private sealed class TestListener(MessageTransport transport) : MessageTransportListener
    {
        private MessageTransport? _transport = transport;
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override bool IsValid => true;
        public override string ListenerName => "test";
        public override ValueTask BindAsync(CancellationToken cancellationToken = default) => default;
        public override ValueTask UnbindAsync(CancellationToken cancellationToken = default) => default;

        public override ValueTask<MessageTransport?> AcceptAsync(CancellationToken cancellationToken = default)
        {
            var result = _transport;
            _transport = null;
            return new(result);
        }
    }
}

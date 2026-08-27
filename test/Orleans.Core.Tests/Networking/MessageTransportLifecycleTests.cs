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
using Orleans.Messaging;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Session;
using Xunit;
using SslApplicationProtocol = System.Net.Security.SslApplicationProtocol;
using SslClientAuthenticationOptions = System.Net.Security.SslClientAuthenticationOptions;

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

    [Fact]
    public void MessageHandlerShared_RejectsHandlerAcquisitionAfterDispose()
    {
        using var serviceProvider = CreateServiceProvider();
        var shared = CreateMessageHandlerShared(serviceProvider);
        shared.Dispose();

        Assert.Throws<ObjectDisposedException>(() => shared.GetSendMessageHandler());
        Assert.Throws<ObjectDisposedException>(() => shared.GetReceiveMessageHandler());
    }

    [Fact]
    public async Task MessageHandlerShared_DisposeWaitsForBorrowedSerializer()
    {
        using var serviceProvider = CreateServiceProvider();
        var shared = CreateMessageHandlerShared(serviceProvider);
        var serializer = shared.GetMessageSerializer();

        var disposeTask = Task.Run(shared.Dispose, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        Assert.False(disposeTask.IsCompleted);

        shared.Return(serializer);

        await disposeTask.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Throws<ObjectDisposedException>(() => shared.GetMessageSerializer());
    }

    [Fact]
    public async Task Connection_CloseAsyncWaitsForActiveSendWorker()
    {
        using var serviceProvider = CreateServiceProvider();
        var shared = CreateMessageHandlerShared(serviceProvider);
        var connectionShared = CreateConnectionCommon(serviceProvider, shared);
        await using var transport = new CapturingTransport();
        using var connection = new BlockingSendConnection(
            transport,
            connectionShared,
            shared.MessageCenter,
            TestContext.Current.CancellationToken);
        connection.Send(new Message());
        await connection.PrepareEntered.WaitAsync(TestContext.Current.CancellationToken);

        var closeTask = connection.CloseAsync(null);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        Assert.False(closeTask.IsCompleted);

        connection.ReleaseSend();
        await closeTask.WaitAsync(TestContext.Current.CancellationToken);

        shared.Dispose();
    }

    [Fact]
    public async Task MessageHandlerShared_DisposeWaitsForQueuedSendWorker()
    {
        using var serviceProvider = CreateServiceProvider();
        var shared = CreateMessageHandlerShared(serviceProvider);
        var connectionShared = CreateConnectionCommon(serviceProvider, shared);
        await using var transport = new CapturingTransport();
        using var connection = new BlockingSendConnection(
            transport,
            connectionShared,
            shared.MessageCenter,
            TestContext.Current.CancellationToken);
        connection.Send(new Message());
        await connection.PrepareEntered.WaitAsync(TestContext.Current.CancellationToken);

        var disposeTask = Task.Run(shared.Dispose, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        Assert.False(disposeTask.IsCompleted);

        connection.ReleaseSend();
        await disposeTask.WaitAsync(TestContext.Current.CancellationToken);
        await connection.CloseAsync(null).WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MessageHandlerShared_DisposeAllowsLeasedSendWorkToFinish()
    {
        using var serviceProvider = CreateServiceProvider();
        var shared = CreateMessageHandlerShared(serviceProvider);
        Assert.True(shared.TryAcquireSendWork());

        var disposeTask = Task.Run(shared.Dispose, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        Assert.False(disposeTask.IsCompleted);

        var request = shared.GetSendMessageHandler();
        request.Reset();
        shared.ReleaseSendWork();

        await disposeTask.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Throws<ObjectDisposedException>(() => shared.GetSendMessageHandler());
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

    [Theory]
    [InlineData(5, false)]
    [InlineData((64 * 1024) + 7, true)]
    public async Task SocketMessageTransport_LinuxIoUring_RoundTripsData(int payloadSize, bool useMultipleBuffers)
    {
        if (!OperatingSystem.IsLinux()
            || !string.Equals(
                Environment.GetEnvironmentVariable("ORLEANS_TEST_IO_URING"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.LocalEndPoint!);
        using var server = await listener.AcceptAsync();
        await using var transport = new SocketMessageTransport(client, NullLogger.Instance, useLinuxIoUring: true);
        var payload = GC.AllocateUninitializedArray<byte>(payloadSize);
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        using var writeRequest = new BufferedWriteRequest(payload, useMultipleBuffers);
        using var readRequest = new FixedLengthReadRequest(payload.Length);

        transport.Start();
        Assert.True(transport.EnqueueWrite(writeRequest));
        var receivedByServer = new byte[payload.Length];
        var receivedLength = 0;
        while (receivedLength < receivedByServer.Length)
        {
            var length = await server.ReceiveAsync(receivedByServer.AsMemory(receivedLength));
            Assert.NotEqual(0, length);
            receivedLength += length;
        }

        await writeRequest.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(transport.EnqueueRead(readRequest));
        var sentLength = 0;
        while (sentLength < payload.Length)
        {
            sentLength += await server.SendAsync(payload.AsMemory(sentLength));
        }

        var receivedByTransport = await readRequest.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(payload.Length, receivedLength);
        Assert.Equal(payload, receivedByServer);
        Assert.Equal(payload.Length, sentLength);
        Assert.Equal(payload, receivedByTransport);
        await transport.CloseAsync(null);
    }

    [Fact]
    public async Task SocketMessageTransport_LinuxIoUring_ConcurrentConnectionsRoundTripData()
    {
        if (!OperatingSystem.IsLinux()
            || !string.Equals(
                Environment.GetEnvironmentVariable("ORLEANS_TEST_IO_URING"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        const int ConnectionCount = 8;
        const int IterationCount = 4;
        const int PayloadSize = (32 * 1024) + 17;
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(ConnectionCount);
        var transports = new SocketMessageTransport[ConnectionCount];
        var serverSockets = new Socket[ConnectionCount];

        try
        {
            for (var i = 0; i < ConnectionCount; i++)
            {
                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                var connect = client.ConnectAsync(listener.LocalEndPoint!);
                serverSockets[i] = await listener.AcceptAsync();
                await connect;
                transports[i] = new SocketMessageTransport(client, NullLogger.Instance, useLinuxIoUring: true);
                transports[i].Start();
            }

            var tasks = new Task[ConnectionCount];
            for (var i = 0; i < tasks.Length; i++)
            {
                tasks[i] = RunConnection(transports[i], serverSockets[i], i);
            }

            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            for (var i = 0; i < transports.Length; i++)
            {
                if (transports[i] is { } transport)
                {
                    await transport.CloseAsync(null);
                    await transport.DisposeAsync();
                }

                serverSockets[i]?.Dispose();
            }
        }

        static async Task RunConnection(SocketMessageTransport transport, Socket server, int connectionId)
        {
            for (var iteration = 0; iteration < IterationCount; iteration++)
            {
                var payload = GC.AllocateUninitializedArray<byte>(PayloadSize);
                for (var i = 0; i < payload.Length; i++)
                {
                    payload[i] = (byte)(i + connectionId + iteration);
                }

                using var writeRequest = new BufferedWriteRequest(payload, useMultipleBuffers: true);
                using var readRequest = new FixedLengthReadRequest(payload.Length);
                Assert.True(transport.EnqueueWrite(writeRequest));
                var receivedByServer = new byte[payload.Length];
                await ReceiveExactly(server, receivedByServer);
                await writeRequest.Completion;
                Assert.Equal(payload, receivedByServer);

                Assert.True(transport.EnqueueRead(readRequest));
                await SendExactly(server, payload);
                var receivedByTransport = await readRequest.Completion;
                Assert.Equal(payload, receivedByTransport);
            }
        }

        static async Task ReceiveExactly(Socket socket, Memory<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                var length = await socket.ReceiveAsync(buffer);
                Assert.NotEqual(0, length);
                buffer = buffer[length..];
            }
        }

        static async Task SendExactly(Socket socket, ReadOnlyMemory<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                var length = await socket.SendAsync(buffer);
                Assert.NotEqual(0, length);
                buffer = buffer[length..];
            }
        }
    }

    [Fact]
    public async Task LinuxIoUringOperation_DisposeWhilePending_Throws()
    {
        if (!OperatingSystem.IsLinux()
            || !string.Equals(
                Environment.GetEnvironmentVariable("ORLEANS_TEST_IO_URING"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connect = client.ConnectAsync(listener.LocalEndPoint!);
        using var server = await listener.AcceptAsync();
        await connect;
        using var receiver = new LinuxIoUringSocketReceiver();
        var buffer = GC.AllocateUninitializedArray<byte>(32, pinned: true);

        var receive = receiver.ReceiveAsync(client, [new ArraySegment<byte>(buffer)]);
        Assert.Throws<InvalidOperationException>(receiver.Dispose);
        Assert.Equal(1, await server.SendAsync(new byte[] { 42 }));
        await receive;

        Assert.Equal(1, receiver.BytesTransferred);
        Assert.Equal(42, buffer[0]);
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
    public async Task TlsConnector_ClientAuthenticationCallbackSeesDefaultApplicationProtocolAndCanSetTargetHost()
    {
        var inner = new TrackingTransport();
        var callbackOptions = new TaskCompletionSource<TlsClientAuthenticationOptions>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = Substitute.For<IOptionsMonitor<TlsOptions>>();
        options.CurrentValue.Returns(new TlsOptions
        {
            ClientCertificateMode = RemoteCertificateMode.NoCertificate,
            OnAuthenticateAsClient = (_, sslOptions) =>
            {
                sslOptions.TargetHost = "localhost";
                callbackOptions.TrySetResult(sslOptions);
            }
        });
        await using var connector = new TlsMessageTransportConnector(new TestConnector(inner), options, NullLoggerFactory.Instance);
        await using var transport = await connector.CreateAsync(
            new IPEndPoint(IPAddress.Loopback, 1),
            TestContext.Current.CancellationToken);

        var configuredOptions = await callbackOptions.Task.WaitAsync(TestContext.Current.CancellationToken);
        var sslOptions = Assert.IsType<SslClientAuthenticationOptions>(configuredOptions.SslClientAuthenticationOptions);

        Assert.Equal("localhost", sslOptions.TargetHost);
        Assert.Equal([new SslApplicationProtocol("orleans")], sslOptions.ApplicationProtocols);
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
        .AddSingleton<NetworkingInstruments>()
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
            () => serviceProvider.GetRequiredService<MessageSerializer>(),
            new MessageFactory(serviceProvider.GetRequiredService<DeepCopier>(), NullLogger<MessageFactory>.Instance, messagingTrace),
            Substitute.For<IMessageCenter>(),
            messagingInstruments);
    }

    private static ConnectionCommon CreateConnectionCommon(IServiceProvider serviceProvider, MessageHandlerShared shared)
    {
        var connectionServices = Substitute.For<IServiceProvider>();
        connectionServices.GetService(typeof(MessageHandlerShared)).Returns(shared);
        return new(
            connectionServices,
            shared.MessageFactory,
            shared.MessagingTrace,
            shared.ConnectionTrace,
            shared.MessagingInstruments,
            serviceProvider.GetRequiredService<NetworkingInstruments>(),
            new NoOpMessageStatisticsSink());
    }

    private sealed class BlockingSendConnection(
        MessageTransport transport,
        ConnectionCommon shared,
        IMessageCenter messageCenter,
        CancellationToken cancellationToken) : Connection(transport, shared), IDisposable
    {
        private readonly TaskCompletionSource _prepareEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _releaseSend = new();

        public Task PrepareEntered => _prepareEntered.Task;
        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.SiloToSilo;
        protected override TimeSpan CloseConnectionTimeout => TimeSpan.FromSeconds(1);
        protected override IMessageCenter MessageCenter => messageCenter;

        public void ReleaseSend() => _releaseSend.Set();

        protected override bool PrepareMessageForSend(Message msg)
        {
            _prepareEntered.TrySetResult();
            _releaseSend.Wait(cancellationToken);
            return true;
        }

        protected override void RetryMessage(Message msg, Exception? ex = null) => msg.Dispose();
        protected internal override void OnReceivedMessage(Message message) { }
        protected internal override void RecordMessageReceive(Message message, int totalBytes, int headerBytes) { }
        protected internal override void RecordMessageSend(Message message, int totalBytes, int headerBytes) { }
        public void Dispose() => _releaseSend.Dispose();
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
        private readonly bool _hasLargeMessages;

        public BufferedWriteRequest(ReadOnlySpan<byte> bytes, bool useMultipleBuffers = false)
        {
            _hasLargeMessages = useMultipleBuffers;
            if (useMultipleBuffers)
            {
                const int SegmentSize = 8 * 1024;
                while (!bytes.IsEmpty)
                {
                    var count = Math.Min(bytes.Length, SegmentSize);
                    _buffer.Write(bytes[..count]);
                    bytes = bytes[count..];
                }
            }
            else
            {
                _buffer.Write(bytes);
            }

            Buffers = new(_buffer);
        }

        internal override bool HasLargeMessages => _hasLargeMessages;

        public Task Completion => _completion.Task;
        public override void SetResult() => _completion.TrySetResult();
        public override void SetException(Exception error) => _completion.TrySetException(error);
        public void Dispose() => _buffer.Dispose();
    }

    private sealed class FixedLengthReadRequest(int length) : ReadRequest, IDisposable
    {
        public TaskCompletionSource<byte[]> CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<byte[]> Completion => CompletionSource.Task;

        public override bool OnRead(ArcBufferReader buffer)
        {
            if (buffer.Length < length)
            {
                return false;
            }

            using var slice = buffer.ConsumeSlice(length);
            var result = new byte[length];
            var offset = 0;
            foreach (var segment in slice)
            {
                segment.CopyTo(result.AsSpan(offset));
                offset += segment.Length;
            }

            CompletionSource.TrySetResult(result);
            return true;
        }

        public override void OnError(Exception error) => CompletionSource.TrySetException(error);

        public override void OnCanceled() => CompletionSource.TrySetCanceled();

        public void Dispose() => CompletionSource.TrySetCanceled();
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

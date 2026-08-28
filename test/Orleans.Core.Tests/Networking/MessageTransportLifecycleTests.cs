#nullable enable
using System;
using System.Buffers;
using System.Buffers.Binary;
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
    public void MessageReadRequest_ReportsFrameLengthWhileAwaitingPayload()
    {
        using var serviceProvider = CreateServiceProvider();
        var request = new MessageReadRequest(CreateMessageHandlerShared(serviceProvider));
        using var writer = new ArcBufferWriter();
        Span<byte> frameHeader = stackalloc byte[Message.LENGTH_HEADER_SIZE];
        BinaryPrimitives.WriteInt32LittleEndian(frameHeader, 123);
        BinaryPrimitives.WriteInt32LittleEndian(frameHeader[sizeof(int)..], 456);
        writer.Write(frameHeader);
        var reader = new ArcBufferReader(writer);

        Assert.False(((IFramedReadRequest)request).OnRead(reader, out var framedLength));
        Assert.Equal(Message.LENGTH_HEADER_SIZE + 123 + 456, framedLength);
        Assert.Equal(0, reader.Length);

        request.Dispose();
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
        await client.ConnectAsync(listener.LocalEndPoint!, TestContext.Current.CancellationToken);
        using var server = await listener.AcceptAsync(TestContext.Current.CancellationToken);
        await using var transport = new SocketMessageTransport(client, NullLogger.Instance, useLinuxIoUring: true);
        Assert.Null(transport.MultishotReceiveStatistics);
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
            var length = await server.ReceiveAsync(receivedByServer.AsMemory(receivedLength), TestContext.Current.CancellationToken);
            Assert.NotEqual(0, length);
            receivedLength += length;
        }

        await writeRequest.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(transport.EnqueueRead(readRequest));
        var sentLength = 0;
        while (sentLength < payload.Length)
        {
            sentLength += await server.SendAsync(payload.AsMemory(sentLength), TestContext.Current.CancellationToken);
        }

        var receivedByTransport = await readRequest.Completion.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(payload.Length, receivedLength);
        Assert.Equal(payload, receivedByServer);
        Assert.Equal(payload.Length, sentLength);
        Assert.Equal(payload, receivedByTransport);
        await transport.CloseAsync(null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LinuxIoUringSocketSender_PinnedSendCompletesSynchronously()
    {
        if (!IsIoUringTestEnabled())
        {
            return;
        }

        var (listener, client, server) = await CreateSocketPair();
        using (listener)
        using (client)
        using (server)
        {
            var sender = new LinuxIoUringSocketSender();
            var payload = GC.AllocateUninitializedArray<byte>(128, pinned: true);
            Random.Shared.NextBytes(payload);

            var send = sender.SendAsync(
                client,
                payload,
                bufferIsPinned: true,
                useZeroCopy: false);

            Assert.True(send.IsCompletedSuccessfully);
            await send;
            var received = new byte[payload.Length];
            var receivedLength = await server.ReceiveAsync(
                received,
                TestContext.Current.CancellationToken);
            Assert.Equal(payload.Length, receivedLength);
            Assert.Equal(payload, received);

            sender.Dispose();
            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = sender.SendAsync(
                    client,
                    payload,
                    bufferIsPinned: true,
                    useZeroCopy: false);
            });
        }
    }

    [Fact]
    public async Task SocketMessageTransport_LinuxIoUringAdaptive_SmallFramesNeverArmMultishot()
    {
        if (!IsIoUringTestEnabled())
        {
            return;
        }

        var (listener, client, server) = await CreateSocketPair();
        using (listener)
        using (client)
        using (server)
        await using (var transport = new SocketMessageTransport(
            client,
            NullLogger.Instance,
            useLinuxIoUring: true,
            linuxIoUringReceiveMode: LinuxIoUringReceiveMode.Adaptive))
        {
            Assert.Null(transport.MultishotReceiveStatistics);
            transport.Start();
            for (var i = 0; i < 12; i++)
            {
                var payload = Enumerable.Repeat((byte)i, 1024).ToArray();
                Assert.Equal(payload, await SendFramedAsync(transport, server, payload));
            }

            Assert.Null(transport.MultishotReceiveStatistics);
            Assert.Equal(0, transport.AdaptivePromotionCount);
            Assert.False(transport.IsAdaptiveMultishot);
            await transport.CloseAsync(null, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task SocketMessageTransport_LinuxIoUringTinyAdaptive_TinyFramesPromoteAndLargeFramesDemote()
    {
        if (!IsIoUringTestEnabled())
        {
            return;
        }

        var (listener, client, server) = await CreateSocketPair();
        using (listener)
        using (client)
        using (server)
        await using (var transport = new SocketMessageTransport(
            client,
            NullLogger.Instance,
            useLinuxIoUring: true,
            linuxIoUringReceiveMode: LinuxIoUringReceiveMode.TinyAdaptive))
        {
            var promotionCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var demotionCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.AdaptiveModeChangedForTesting = useMultishot =>
            {
                (useMultishot ? promotionCompleted : demotionCompleted).TrySetResult();
            };

            transport.Start();
            for (var i = 0; i < 8; i++)
            {
                var payload = Enumerable.Repeat((byte)i, 128).ToArray();
                Assert.Equal(payload, await SendFramedAsync(transport, server, payload));
            }

            await promotionCompleted.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            Assert.True(transport.IsAdaptiveMultishot);
            Assert.Equal(1, transport.AdaptivePromotionCount);

            var largePayload = Enumerable.Repeat((byte)42, 16 * 1024).ToArray();
            var queuedTinyPayload = Enumerable.Repeat((byte)43, 128).ToArray();
            using var largeRequest = new FramedReadRequest(largePayload.Length);
            using var queuedTinyRequest = new FramedReadRequest(queuedTinyPayload.Length);
            Assert.True(transport.EnqueueRead(largeRequest));
            Assert.True(transport.EnqueueRead(queuedTinyRequest));
            await SendExactly(server, CreateFrame(largePayload).Concat(CreateFrame(queuedTinyPayload)).ToArray());
            Assert.Equal(
                largePayload,
                await largeRequest.Completion.WaitAsync(
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken));
            Assert.Equal(
                queuedTinyPayload,
                await queuedTinyRequest.Completion.WaitAsync(
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken));
            await demotionCompleted.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            Assert.False(transport.IsAdaptiveMultishot);
            Assert.Equal(1, transport.AdaptiveDemotionCount);

            for (var i = 0; i < 12; i++)
            {
                var payload = Enumerable.Repeat((byte)i, 1024).ToArray();
                Assert.Equal(payload, await SendFramedAsync(transport, server, payload));
            }

            largePayload[0] = 43;
            Assert.Equal(largePayload, await SendFramedAsync(transport, server, largePayload));
            Assert.False(transport.IsAdaptiveMultishot);
            Assert.Equal(1, transport.AdaptivePromotionCount);
            await transport.CloseAsync(null, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task SocketMessageTransport_LinuxIoUringAdaptive_TwoConsecutiveLargeFramesPromote()
    {
        if (!IsIoUringTestEnabled())
        {
            return;
        }

        var (listener, client, server) = await CreateSocketPair();
        using (listener)
        using (client)
        using (server)
        await using (var transport = new SocketMessageTransport(
            client,
            NullLogger.Instance,
            useLinuxIoUring: true,
            linuxIoUringReceiveMode: LinuxIoUringReceiveMode.Adaptive))
        {
            var promotionCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.AdaptiveModeChangedForTesting = useMultishot =>
            {
                if (useMultishot)
                {
                    promotionCompleted.TrySetResult();
                }
            };

            transport.Start();
            var startupPayload = Enumerable.Repeat((byte)41, 86 * 1024).ToArray();
            Assert.Equal(startupPayload, await SendFramedAsync(transport, server, startupPayload));
            Assert.Null(transport.MultishotReceiveStatistics);
            Assert.False(transport.IsAdaptiveMultishot);

            using (var unframedRequest = new FixedLengthReadRequest(1))
            {
                Assert.True(transport.EnqueueRead(unframedRequest));
                Assert.Equal(1, await server.SendAsync(new byte[] { 42 }));
                Assert.Equal(
                    new byte[] { 42 },
                    await unframedRequest.Completion.WaitAsync(
                        TimeSpan.FromSeconds(10),
                        TestContext.Current.CancellationToken));
            }

            var payload = Enumerable.Repeat((byte)43, 16 * 1024).ToArray();
            Assert.Equal(payload, await SendFramedAsync(transport, server, payload));
            Assert.Null(transport.MultishotReceiveStatistics);
            Assert.False(transport.IsAdaptiveMultishot);

            using (var unknownFramedRequest = new UnknownFramedReadRequest(1))
            {
                Assert.True(transport.EnqueueRead(unknownFramedRequest));
                Assert.Equal(1, await server.SendAsync(new byte[] { 43 }));
                Assert.Equal(
                    new byte[] { 43 },
                    await unknownFramedRequest.Completion.WaitAsync(
                        TimeSpan.FromSeconds(10),
                        TestContext.Current.CancellationToken));
            }

            payload[0] = 44;
            Assert.Equal(payload, await SendFramedAsync(transport, server, payload));
            Assert.Null(transport.MultishotReceiveStatistics);
            Assert.False(transport.IsAdaptiveMultishot);

            var smallPayload = Enumerable.Repeat((byte)42, 1024).ToArray();
            Assert.Equal(smallPayload, await SendFramedAsync(transport, server, smallPayload));
            payload[0] = 45;
            Assert.Equal(payload, await SendFramedAsync(transport, server, payload));
            Assert.Null(transport.MultishotReceiveStatistics);
            Assert.False(transport.IsAdaptiveMultishot);

            payload[0] = 46;
            Assert.Equal(payload, await SendFramedAsync(transport, server, payload));
            await promotionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(transport.IsAdaptiveMultishot);
            Assert.Equal(1, transport.AdaptivePromotionCount);
            Assert.Equal(0, transport.AdaptiveDemotionCount);
            await transport.CloseAsync(null, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task SocketMessageTransport_LinuxIoUringAdaptive_RepeatedLargeFramesKeepOneReceiveActive()
    {
        if (!IsIoUringTestEnabled())
        {
            return;
        }

        var (listener, client, server) = await CreateSocketPair();
        using (listener)
        using (client)
        using (server)
        await using (var transport = new SocketMessageTransport(
            client,
            NullLogger.Instance,
            useLinuxIoUring: true,
            linuxIoUringReceiveMode: LinuxIoUringReceiveMode.Adaptive))
        {
            transport.Start();
            var payload = Enumerable.Repeat((byte)43, 32 * 1024).ToArray();
            Assert.Equal(payload, await SendFramedAsync(transport, server, payload));
            payload[0] = 44;
            Assert.Equal(payload, await SendFramedAsync(transport, server, payload));

            for (var i = 0; i < 4; i++)
            {
                payload[0] = (byte)i;
                Assert.Equal(payload, await SendFramedAsync(transport, server, payload));
            }

            var statistics = Assert.IsType<(
                long AdoptedPages,
                long CompletedSegments,
                long FinalBuffers,
                long ReplacementPages,
                long NoBufferCompletions,
                long PayloadCopies,
                ushort BufferGroup,
                long ReceiveStarts)>(
                transport.MultishotReceiveStatistics);
            Assert.Equal(1, statistics.ReceiveStarts);
            Assert.Equal(1, transport.AdaptivePromotionCount);
            Assert.Equal(0, transport.AdaptiveDemotionCount);
            await transport.CloseAsync(null, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task SocketMessageTransport_LinuxIoUringAdaptive_LargeToSmallPreservesOrderAndDemotes()
    {
        if (!IsIoUringTestEnabled())
        {
            return;
        }

        var (listener, client, server) = await CreateSocketPair();
        using (listener)
        using (client)
        using (server)
        await using (var transport = new SocketMessageTransport(
            client,
            NullLogger.Instance,
            useLinuxIoUring: true,
            linuxIoUringReceiveMode: LinuxIoUringReceiveMode.Adaptive))
        {
            var demotionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var allowDemotion = new ManualResetEventSlim();
            var demotionCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondPromotionCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var promotionCount = 0;
            transport.AdaptiveDemotionStartingForTesting = () =>
            {
                demotionStarted.TrySetResult();
                if (!allowDemotion.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Timed out waiting to continue adaptive receive demotion.");
                }
            };
            transport.AdaptiveModeChangedForTesting = useMultishot =>
            {
                if (useMultishot)
                {
                    if (Interlocked.Increment(ref promotionCount) == 2)
                    {
                        secondPromotionCompleted.TrySetResult();
                    }
                }
                else
                {
                    demotionCompleted.TrySetResult();
                }
            };

            transport.Start();
            var largePayload = Enumerable.Repeat((byte)44, 32 * 1024).ToArray();
            Assert.Equal(largePayload, await SendFramedAsync(transport, server, largePayload));
            largePayload[0] = 45;
            Assert.Equal(largePayload, await SendFramedAsync(transport, server, largePayload));

            var requests = new FramedReadRequest[8];
            var frames = new byte[8][];
            for (var i = 0; i < requests.Length; i++)
            {
                var payload = Enumerable.Repeat((byte)(50 + i), 257).ToArray();
                requests[i] = new FramedReadRequest(payload.Length);
                frames[i] = CreateFrame(payload);
                Assert.True(transport.EnqueueRead(requests[i]));
            }

            var batch = new byte[frames.Sum(static frame => frame.Length)];
            var batchOffset = 0;
            foreach (var frame in frames)
            {
                frame.CopyTo(batch, batchOffset);
                batchOffset += frame.Length;
            }

            await SendExactly(server, batch);
            await demotionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            using var finalRequest = new FramedReadRequest(129);
            var finalPayload = Enumerable.Repeat((byte)99, 129).ToArray();
            Assert.True(transport.EnqueueRead(finalRequest));
            var finalSend = SendExactly(server, CreateFrame(finalPayload));
            allowDemotion.Set();

            for (var i = 0; i < requests.Length; i++)
            {
                var expected = Enumerable.Repeat((byte)(50 + i), 257).ToArray();
                Assert.Equal(
                    expected,
                    await requests[i].Completion.WaitAsync(
                        TimeSpan.FromSeconds(10),
                        TestContext.Current.CancellationToken));
                requests[i].Dispose();
            }

            await finalSend;
            Assert.Equal(
                finalPayload,
                await finalRequest.Completion.WaitAsync(
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken));
            await demotionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(transport.IsAdaptiveMultishot);
            Assert.Equal(1, transport.AdaptiveDemotionCount);

            var nextLargePayload = Enumerable.Repeat((byte)100, 32 * 1024).ToArray();
            Assert.Equal(nextLargePayload, await SendFramedAsync(transport, server, nextLargePayload));
            Assert.False(transport.IsAdaptiveMultishot);
            nextLargePayload[0] = 101;
            Assert.Equal(nextLargePayload, await SendFramedAsync(transport, server, nextLargePayload));
            await secondPromotionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(transport.IsAdaptiveMultishot);
            Assert.Equal(2, transport.AdaptivePromotionCount);
            var multishotProbe = Enumerable.Repeat((byte)102, 193).ToArray();
            Assert.Equal(multishotProbe, await SendFramedAsync(transport, server, multishotProbe));
            Assert.Equal(2, transport.MultishotReceiveStatistics?.ReceiveStarts);
            await transport.CloseAsync(null, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task SocketMessageTransport_LinuxIoUringAdaptive_BacklogDoesNotHotDemote()
    {
        if (!IsIoUringTestEnabled())
        {
            return;
        }

        var (listener, client, server) = await CreateSocketPair();
        using (listener)
        using (client)
        using (server)
        await using (var transport = new SocketMessageTransport(
            client,
            NullLogger.Instance,
            useLinuxIoUring: true,
            linuxIoUringReceiveMode: LinuxIoUringReceiveMode.Adaptive))
        {
            var blockedRequestEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var allowBlockedRequest = new ManualResetEventSlim();
            var demotionCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.AdaptiveModeChangedForTesting = useMultishot =>
            {
                if (!useMultishot)
                {
                    demotionCompleted.TrySetResult();
                }
            };

            transport.Start();
            var largePayload = Enumerable.Repeat((byte)61, 32 * 1024).ToArray();
            Assert.Equal(largePayload, await SendFramedAsync(transport, server, largePayload));
            largePayload[0] = 62;
            Assert.Equal(largePayload, await SendFramedAsync(transport, server, largePayload));

            var requests = new FramedReadRequest[9];
            var frames = new byte[requests.Length][];
            for (var i = 0; i < requests.Length; i++)
            {
                var payload = Enumerable.Repeat((byte)(70 + i), 257).ToArray();
                requests[i] = new FramedReadRequest(
                    payload.Length,
                    beforeComplete: i == requests.Length - 1
                        ? () =>
                        {
                            blockedRequestEntered.TrySetResult();
                            if (!allowBlockedRequest.Wait(TimeSpan.FromSeconds(10)))
                            {
                                throw new TimeoutException("Timed out waiting to complete the backlogged read.");
                            }
                        }
                        : null);
                frames[i] = CreateFrame(payload);
                Assert.True(transport.EnqueueRead(requests[i]));
            }

            var batch = new byte[frames.Sum(static frame => frame.Length)];
            var offset = 0;
            foreach (var frame in frames)
            {
                frame.CopyTo(batch, offset);
                offset += frame.Length;
            }

            var send = SendExactly(server, batch);
            await blockedRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(transport.IsAdaptiveMultishot);
            Assert.Equal(0, transport.AdaptiveDemotionCount);

            allowBlockedRequest.Set();
            await send;
            for (var i = 0; i < requests.Length; i++)
            {
                var expected = Enumerable.Repeat((byte)(70 + i), 257).ToArray();
                Assert.Equal(
                    expected,
                    await requests[i].Completion.WaitAsync(
                        TimeSpan.FromSeconds(10),
                        TestContext.Current.CancellationToken));
                requests[i].Dispose();
            }

            await demotionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var finalPayload = Enumerable.Repeat((byte)90, 193).ToArray();
            Assert.Equal(finalPayload, await SendFramedAsync(transport, server, finalPayload));
            Assert.False(transport.IsAdaptiveMultishot);
            await transport.CloseAsync(null, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task SocketMessageTransport_LinuxIoUringAdaptive_CloseRacesLazyPromotion()
    {
        if (!IsIoUringTestEnabled())
        {
            return;
        }

        var (listener, client, server) = await CreateSocketPair();
        using (listener)
        using (client)
        using (server)
        await using (var transport = new SocketMessageTransport(
            client,
            NullLogger.Instance,
            useLinuxIoUring: true,
            linuxIoUringReceiveMode: LinuxIoUringReceiveMode.Adaptive))
        {
            transport.Start();
            var payload = Enumerable.Repeat((byte)91, 32 * 1024).ToArray();
            Assert.Equal(payload, await SendFramedAsync(transport, server, payload));
            Assert.Null(transport.MultishotReceiveStatistics);

            var frameObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var allowFrameObservation = new ManualResetEventSlim();
            using var request = new FramedReadRequest(
                payload.Length,
                frameLengthObserved: () =>
                {
                    frameObserved.TrySetResult();
                    if (!allowFrameObservation.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("Timed out waiting to continue lazy promotion.");
                    }
                });
            Assert.True(transport.EnqueueRead(request));
            payload[0] = 92;
            var send = SendExactly(server, CreateFrame(payload));
            await frameObserved.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            var close = transport.CloseAsync(null, TestContext.Current.CancellationToken).AsTask();
            allowFrameObservation.Set();

            await send;
            await close.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Null(transport.MultishotReceiveStatistics);
            Assert.Equal(0, transport.AdaptivePromotionCount);
            Assert.False(transport.IsAdaptiveMultishot);
        }
    }

    [Fact]
    public async Task SocketMessageTransport_LinuxIoUringAdaptive_CloseAfterLazyPublicationDoesNotArm()
    {
        if (!IsIoUringTestEnabled())
        {
            return;
        }

        var (listener, client, server) = await CreateSocketPair();
        using (listener)
        using (client)
        using (server)
        await using (var transport = new SocketMessageTransport(
            client,
            NullLogger.Instance,
            useLinuxIoUring: true,
            linuxIoUringReceiveMode: LinuxIoUringReceiveMode.Adaptive))
        {
            var receiverPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var allowPromotion = new ManualResetEventSlim();
            transport.AdaptiveModeChangedForTesting = useMultishot =>
            {
                if (useMultishot)
                {
                    receiverPublished.TrySetResult();
                    if (!allowPromotion.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("Timed out waiting to continue published lazy promotion.");
                    }
                }
            };

            transport.Start();
            var payload = Enumerable.Repeat((byte)93, 32 * 1024).ToArray();
            Assert.Equal(payload, await SendFramedAsync(transport, server, payload));

            using var request = new FramedReadRequest(payload.Length);
            Assert.True(transport.EnqueueRead(request));
            await SendExactly(server, CreateFrame(payload).AsMemory(0, sizeof(int) * 2));
            await receiverPublished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            var close = transport.CloseAsync(null, TestContext.Current.CancellationToken).AsTask();
            allowPromotion.Set();

            await close.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => request.Completion.WaitAsync(
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken));
            var statistics = Assert.IsType<(
                long AdoptedPages,
                long CompletedSegments,
                long FinalBuffers,
                long ReplacementPages,
                long NoBufferCompletions,
                long PayloadCopies,
                ushort BufferGroup,
                long ReceiveStarts)>(
                transport.MultishotReceiveStatistics);
            Assert.Equal(0, statistics.ReceiveStarts);
            Assert.False(transport.IsSocketReceivePending);
        }
    }

    [Fact]
    public async Task SocketMessageTransport_LinuxIoUringAdaptive_AlternatingSizesDoesNotThrash()
    {
        if (!IsIoUringTestEnabled())
        {
            return;
        }

        var (listener, client, server) = await CreateSocketPair();
        using (listener)
        using (client)
        using (server)
        await using (var transport = new SocketMessageTransport(
            client,
            NullLogger.Instance,
            useLinuxIoUring: true,
            linuxIoUringReceiveMode: LinuxIoUringReceiveMode.Adaptive))
        {
            transport.Start();
            var largePayload = Enumerable.Repeat((byte)71, 32 * 1024).ToArray();
            Assert.Equal(largePayload, await SendFramedAsync(transport, server, largePayload));
            largePayload[0] = 72;
            Assert.Equal(largePayload, await SendFramedAsync(transport, server, largePayload));

            for (var i = 0; i < 12; i++)
            {
                var size = (i & 1) == 0 ? 1024 : 32 * 1024;
                var payload = Enumerable.Repeat((byte)i, size).ToArray();
                Assert.Equal(payload, await SendFramedAsync(transport, server, payload));
            }

            var statistics = Assert.IsType<(
                long AdoptedPages,
                long CompletedSegments,
                long FinalBuffers,
                long ReplacementPages,
                long NoBufferCompletions,
                long PayloadCopies,
                ushort BufferGroup,
                long ReceiveStarts)>(
                transport.MultishotReceiveStatistics);
            Assert.True(transport.IsAdaptiveMultishot);
            Assert.Equal(1, transport.AdaptivePromotionCount);
            Assert.Equal(0, transport.AdaptiveDemotionCount);
            Assert.Equal(1, statistics.ReceiveStarts);
            await transport.CloseAsync(null, TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SocketMessageTransport_LinuxIoUringAdaptive_CloseCancelsPendingRead(bool promote)
    {
        if (!IsIoUringTestEnabled())
        {
            return;
        }

        var (listener, client, server) = await CreateSocketPair();
        using (listener)
        using (client)
        using (server)
        await using (var transport = new SocketMessageTransport(
            client,
            NullLogger.Instance,
            useLinuxIoUring: true,
            linuxIoUringReceiveMode: LinuxIoUringReceiveMode.Adaptive))
        {
            transport.Start();
            if (promote)
            {
                var payload = Enumerable.Repeat((byte)72, 32 * 1024).ToArray();
                Assert.Equal(payload, await SendFramedAsync(transport, server, payload));
                payload[0] = 73;
                Assert.Equal(payload, await SendFramedAsync(transport, server, payload));
            }

            using var request = new FramedReadRequest(128);
            Assert.True(transport.EnqueueRead(request));
            Assert.True(SpinWait.SpinUntil(() => transport.IsSocketReceivePending, TimeSpan.FromSeconds(10)));

            await transport.CloseAsync(null, TestContext.Current.CancellationToken).AsTask().WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => request.Completion.WaitAsync(
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SocketMessageTransport_LinuxIoUringAdaptive_FinCancelsPendingRead(bool promote)
    {
        if (!IsIoUringTestEnabled())
        {
            return;
        }

        var (listener, client, server) = await CreateSocketPair();
        using (listener)
        using (client)
        using (server)
        await using (var transport = new SocketMessageTransport(
            client,
            NullLogger.Instance,
            useLinuxIoUring: true,
            linuxIoUringReceiveMode: LinuxIoUringReceiveMode.Adaptive))
        {
            transport.Start();
            if (promote)
            {
                var payload = Enumerable.Repeat((byte)73, 32 * 1024).ToArray();
                Assert.Equal(payload, await SendFramedAsync(transport, server, payload));
                payload[0] = 74;
                Assert.Equal(payload, await SendFramedAsync(transport, server, payload));
            }

            using var request = new FramedReadRequest(128);
            Assert.True(transport.EnqueueRead(request));
            server.Shutdown(SocketShutdown.Send);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => request.Completion.WaitAsync(
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken));
            await transport.CloseAsync(null, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task SocketMessageTransport_LinuxIoUringMultishot_RoundTripsFragmentedDataAndRearms()
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
        await client.ConnectAsync(listener.LocalEndPoint!, TestContext.Current.CancellationToken);
        using var server = await listener.AcceptAsync(TestContext.Current.CancellationToken);
        await using var transport = new SocketMessageTransport(
            client,
            NullLogger.Instance,
            useLinuxIoUring: true,
            linuxIoUringReceiveMode: LinuxIoUringReceiveMode.Multishot);
        Assert.NotNull(transport.MultishotReceiveStatistics);
        transport.Start();

        for (var iteration = 0; iteration < 2; iteration++)
        {
            var payload = GC.AllocateUninitializedArray<byte>((32 * 16 * 1024) + 37);
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i + iteration);
            }

            using var request = new FixedLengthReadRequest(payload.Length);
            Assert.True(transport.EnqueueRead(request));
            var offset = 0;
            while (offset < payload.Length)
            {
                var count = Math.Min(997, payload.Length - offset);
                var sent = await server.SendAsync(
                    payload.AsMemory(offset, count),
                    TestContext.Current.CancellationToken);
                Assert.Equal(count, sent);
                offset += sent;
            }

            var received = await request.Completion.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            Assert.Equal(payload, received);
        }

        var statistics = Assert.IsType<(
            long AdoptedPages,
            long CompletedSegments,
            long FinalBuffers,
            long ReplacementPages,
            long NoBufferCompletions,
            long PayloadCopies,
            ushort BufferGroup,
            long ReceiveStarts)>(
            transport.MultishotReceiveStatistics);
        Assert.True(statistics.AdoptedPages > 16);
        Assert.True(statistics.CompletedSegments > statistics.AdoptedPages);
        Assert.True(statistics.FinalBuffers > 16);
        Assert.True(statistics.ReplacementPages > 0);
        Assert.Equal(0, statistics.PayloadCopies);
        Assert.True(statistics.ReceiveStarts >= 1);
        await transport.CloseAsync(null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LinuxIoUringMultishot_TinyFragmentsSharePageAndEarlySliceSurvivesCancellation()
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
        await client.ConnectAsync(listener.LocalEndPoint!, TestContext.Current.CancellationToken);
        using var server = await listener.AcceptAsync(TestContext.Current.CancellationToken);
        var writer = new ArcBufferWriter();
        var receiver = new LinuxIoUringSocketMultishotReceiver();
        ArcBuffer earlySlice = default;
        ArcBufferPage? page = null;
        var pageVersion = 0;
        var earlySliceDisposed = false;
        var receiverDisposed = false;
        var writerDisposed = false;

        try
        {
            for (var i = 0; i < 64; i++)
            {
                var receive = receiver.ReceiveAsync(client, writer);
                Assert.Equal(1, await server.SendAsync(new byte[] { (byte)i }));
                await receive.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.Equal(1, receiver.BytesTransferred);

                if (i == 0)
                {
                    earlySlice = writer.ConsumeSlice(1);
                    page = earlySlice.First;
                    pageVersion = page.Version;
                }
                else
                {
                    using var slice = writer.ConsumeSlice(1);
                    Assert.Same(page, slice.First);
                    Assert.Equal(new byte[] { (byte)i }, slice.ToArray());
                }
            }

            Assert.NotNull(page);
            Assert.Equal(new byte[] { 0 }, earlySlice.ToArray());
            Assert.Equal(1, receiver.AdoptedPageCount);
            Assert.Equal(64, receiver.CompletedSegmentCount);
            Assert.Equal(0, receiver.FinalBufferCount);
            Assert.Equal(1, receiver.ActiveIncrementalPageCount);
            Assert.Equal(0, receiver.PayloadCopyCount);

            var pendingReceive = receiver.ReceiveAsync(client, writer);
            await receiver.StopAsync().AsTask().WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<SocketException>(
                () => pendingReceive.AsTask().WaitAsync(
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken));
            receiver.Dispose();
            receiverDisposed = true;
            writer.Dispose();
            writerDisposed = true;

            Assert.Equal(1, page.ReferenceCount);
            Assert.Equal(new byte[] { 0 }, earlySlice.ToArray());
            earlySlice.Dispose();
            earlySliceDisposed = true;
            Assert.Equal(0, page.ReferenceCount);
            Assert.Equal(pageVersion + 1, page.Version);
        }
        finally
        {
            if (!receiverDisposed && receiver.IsPending)
            {
                await receiver.StopAsync();
            }

            if (!earlySliceDisposed)
            {
                earlySlice.Dispose();
            }

            if (!receiverDisposed && !receiver.IsPending)
            {
                receiver.Dispose();
            }

            if (!writerDisposed)
            {
                writer.Dispose();
            }
        }
    }

    [Fact]
    public async Task LinuxIoUringMultishot_ExhaustsRingThenDemandRefillsForLargeFrame()
    {
        if (!OperatingSystem.IsLinux()
            || !string.Equals(
                Environment.GetEnvironmentVariable("ORLEANS_TEST_IO_URING"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        const int PayloadSize = (17 * 16 * 1024) + 37;
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.LocalEndPoint!, TestContext.Current.CancellationToken);
        using var server = await listener.AcceptAsync(TestContext.Current.CancellationToken);
        using var writer = new ArcBufferWriter();
        using var receiver = new LinuxIoUringSocketMultishotReceiver();
        var payload = GC.AllocateUninitializedArray<byte>(PayloadSize);
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        var firstReceive = receiver.ReceiveAsync(client, writer);
        await SendExactly(server, payload).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await firstReceive.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(
            SpinWait.SpinUntil(
                () => receiver.NoBufferCompletionCount > 0,
                TimeSpan.FromSeconds(10)),
            "The multishot receive did not report ENOBUFS after all 16 provided buffers were consumed.");

        while (writer.Length < payload.Length)
        {
            await receiver.ReceiveAsync(client, writer).AsTask().WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
        }

        using var slice = writer.ConsumeSlice(payload.Length);
        Assert.Equal(payload, slice.ToArray());
        Assert.True(receiver.AdoptedPageCount > 16);
        Assert.True(receiver.CompletedSegmentCount >= receiver.AdoptedPageCount);
        Assert.True(receiver.ReplacementPageCount > 0);
        Assert.True(receiver.NoBufferCompletionCount > 0);
        Assert.Equal(0, receiver.PayloadCopyCount);
        await receiver.StopAsync().AsTask().WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

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
    public async Task LinuxIoUringMultishot_FinalSegmentReleasesReceiverReference()
    {
        if (!OperatingSystem.IsLinux()
            || !string.Equals(
                Environment.GetEnvironmentVariable("ORLEANS_TEST_IO_URING"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        const int PayloadSize = 16 * 1024;
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.LocalEndPoint!, TestContext.Current.CancellationToken);
        using var server = await listener.AcceptAsync(TestContext.Current.CancellationToken);
        var writer = new ArcBufferWriter();
        var receiver = new LinuxIoUringSocketMultishotReceiver();
        var payload = GC.AllocateUninitializedArray<byte>(PayloadSize);
        Random.Shared.NextBytes(payload);

        var receive = receiver.ReceiveAsync(client, writer);
        await SendExactly(server, payload).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await receive.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        while (writer.Length < payload.Length)
        {
            await receiver.ReceiveAsync(client, writer).AsTask().WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
        }

        var page = Assert.IsType<ArcBufferPage>(receiver.FirstAdoptedPage);
        var pageVersion = page.Version;
        Assert.Equal(1, receiver.AdoptedPageCount);
        Assert.Equal(1, receiver.FinalBufferCount);
        Assert.Equal(0, receiver.ActiveIncrementalPageCount);
        Assert.Equal(1, page.ReferenceCount);
        using (var slice = writer.ConsumeSlice(payload.Length))
        {
            Assert.Same(page, slice.First);
            Assert.Equal(payload, slice.ToArray());
        }

        await receiver.StopAsync().AsTask().WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        receiver.Dispose();
        writer.Dispose();
        Assert.Equal(0, page.ReferenceCount);
        Assert.Equal(pageVersion + 1, page.Version);

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
    public async Task LinuxIoUringMultishot_AdoptedPagesOutliveReceiver()
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
        await client.ConnectAsync(listener.LocalEndPoint!, TestContext.Current.CancellationToken);
        using var server = await listener.AcceptAsync(TestContext.Current.CancellationToken);
        var writer = new ArcBufferWriter();
        var receiver = new LinuxIoUringSocketMultishotReceiver();
        var payload = GC.AllocateUninitializedArray<byte>((16 * 16 * 1024) + 29);
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        var sendTask = SendExactly(server, payload);
        while (writer.Length < payload.Length)
        {
            await receiver.ReceiveAsync(client, writer).AsTask().WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
        }

        await sendTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var slice = writer.ConsumeSlice(payload.Length);
        Assert.Same(receiver.FirstAdoptedPage, slice.First);
        Assert.Equal(0, receiver.PayloadCopyCount);
        Assert.True(receiver.AdoptedPageCount > 8);
        Assert.True(receiver.CompletedSegmentCount >= receiver.AdoptedPageCount);

        await receiver.StopAsync().AsTask().WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        receiver.Dispose();
        writer.Dispose();

        Assert.Equal(payload, slice.ToArray());
        slice.Dispose();

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
    public async Task LinuxIoUringMultishot_BufferGroupIsReleasedAfterRingUnregister()
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
        await client.ConnectAsync(listener.LocalEndPoint!, TestContext.Current.CancellationToken);
        using var server = await listener.AcceptAsync(TestContext.Current.CancellationToken);
        using var writer = new ArcBufferWriter();
        LinuxIoUringEngine engine;
        ushort releasedGroup;

        using (var first = new LinuxIoUringSocketMultishotReceiver())
        {
            engine = first.Engine;
            releasedGroup = first.BufferGroup;
            var receive = first.ReceiveAsync(client, writer);
            Assert.Equal(1, await server.SendAsync(new byte[] { 42 }));
            await receive.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            using (var second = new LinuxIoUringSocketMultishotReceiver(engine))
            {
                Assert.NotEqual(releasedGroup, second.BufferGroup);
            }

            await first.StopAsync().AsTask().WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            using var third = new LinuxIoUringSocketMultishotReceiver(engine);
            Assert.NotEqual(releasedGroup, third.BufferGroup);
        }

        using var replacement = new LinuxIoUringSocketMultishotReceiver(engine);
        Assert.Equal(releasedGroup, replacement.BufferGroup);
    }

    [Fact]
    public async Task SocketMessageTransport_LinuxIoUringAdaptive_ConcurrentConnectionsRoundTripData()
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
                var connect = client.ConnectAsync(listener.LocalEndPoint!, TestContext.Current.CancellationToken);
                serverSockets[i] = await listener.AcceptAsync(TestContext.Current.CancellationToken);
                await connect;
                transports[i] = new SocketMessageTransport(
                    client,
                    NullLogger.Instance,
                    useLinuxIoUring: true,
                    linuxIoUringReceiveMode: LinuxIoUringReceiveMode.Adaptive);
                transports[i].Start();
            }

            var tasks = new Task[ConnectionCount];
            for (var i = 0; i < tasks.Length; i++)
            {
                tasks[i] = RunConnection(transports[i], serverSockets[i], i);
            }

            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            foreach (var transport in transports)
            {
                Assert.True(transport.IsAdaptiveMultishot);
                Assert.Equal(1, transport.AdaptivePromotionCount);
                Assert.True(transport.MultishotReceiveStatistics?.ReceiveStarts >= 1);
            }
        }
        finally
        {
            for (var i = 0; i < transports.Length; i++)
            {
                if (transports[i] is { } transport)
                {
                    await transport.CloseAsync(null, TestContext.Current.CancellationToken);
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
                using var readRequest = new FramedReadRequest(payload.Length);
                Assert.True(transport.EnqueueWrite(writeRequest));
                var receivedByServer = new byte[payload.Length];
                await ReceiveExactly(server, receivedByServer);
                await writeRequest.Completion;
                Assert.Equal(payload, receivedByServer);

                Assert.True(transport.EnqueueRead(readRequest));
                await SendExactly(server, CreateFrame(payload));
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
        var connect = client.ConnectAsync(listener.LocalEndPoint!, TestContext.Current.CancellationToken);
        using var server = await listener.AcceptAsync(TestContext.Current.CancellationToken);
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
    public async Task SocketMessageTransport_LinuxIoUring_DisposeBeforeStart_Closes()
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
        var connect = client.ConnectAsync(listener.LocalEndPoint!, TestContext.Current.CancellationToken);
        using var server = await listener.AcceptAsync(TestContext.Current.CancellationToken);
        await connect;
        var transport = new SocketMessageTransport(client, NullLogger.Instance, useLinuxIoUring: true);

        await transport.DisposeAsync();

        Assert.True(transport.Closed.IsCancellationRequested);
    }

    [Fact]
    public async Task SocketMessageTransport_LinuxIoUringMultishot_DisposeBeforeStart_Closes()
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
        var connect = client.ConnectAsync(listener.LocalEndPoint!, TestContext.Current.CancellationToken);
        using var server = await listener.AcceptAsync(TestContext.Current.CancellationToken);
        await connect;
        var transport = new SocketMessageTransport(
            client,
            NullLogger.Instance,
            useLinuxIoUring: true,
            linuxIoUringReceiveMode: LinuxIoUringReceiveMode.Multishot);

        await transport.DisposeAsync();

        Assert.True(transport.Closed.IsCancellationRequested);
    }

    [Fact]
    public async Task SocketMessageTransport_LinuxIoUringMultishot_FinCancelsPendingRead()
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
        await client.ConnectAsync(listener.LocalEndPoint!, TestContext.Current.CancellationToken);
        using var server = await listener.AcceptAsync(TestContext.Current.CancellationToken);
        await using var transport = new SocketMessageTransport(
            client,
            NullLogger.Instance,
            useLinuxIoUring: true,
            linuxIoUringReceiveMode: LinuxIoUringReceiveMode.Multishot);
        using var request = new FixedLengthReadRequest(1);

        transport.Start();
        Assert.True(transport.EnqueueRead(request));
        server.Shutdown(SocketShutdown.Send);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => request.Completion.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken));
        await transport.CloseAsync(null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SocketMessageTransport_LinuxIoUringMultishot_CloseCancelsPendingRead()
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
        await client.ConnectAsync(listener.LocalEndPoint!, TestContext.Current.CancellationToken);
        using var server = await listener.AcceptAsync(TestContext.Current.CancellationToken);
        await using var transport = new SocketMessageTransport(
            client,
            NullLogger.Instance,
            useLinuxIoUring: true,
            linuxIoUringReceiveMode: LinuxIoUringReceiveMode.Multishot);
        using var request = new FixedLengthReadRequest(1);

        transport.Start();
        Assert.True(transport.EnqueueRead(request));
        Assert.True(SpinWait.SpinUntil(() => transport.IsSocketReceivePending, TimeSpan.FromSeconds(10)));

        await transport.CloseAsync(null, TestContext.Current.CancellationToken).AsTask().WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => request.Completion.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SocketMessageTransport_LinuxIoUring_NonLoopbackUsesZeroCopy()
    {
        if (!OperatingSystem.IsLinux()
            || !string.Equals(
                Environment.GetEnvironmentVariable("ORLEANS_TEST_IO_URING"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var address = (await Dns.GetHostAddressesAsync(Dns.GetHostName(), TestContext.Current.CancellationToken))
            .FirstOrDefault(static address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
        if (address is null)
        {
            return;
        }

        const int PayloadSize = (64 * 1024) + 7;
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(address, 0));
        listener.Listen(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connect = client.ConnectAsync(listener.LocalEndPoint!, TestContext.Current.CancellationToken);
        using var server = await listener.AcceptAsync(TestContext.Current.CancellationToken);
        await connect;
        await using var transport = new SocketMessageTransport(client, NullLogger.Instance, useLinuxIoUring: true);
        var payload = GC.AllocateUninitializedArray<byte>(PayloadSize);
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        using var writeRequest = new BufferedWriteRequest(payload, useMultipleBuffers: true);
        var initialStatistics = LinuxIoUringEngine.GetZeroCopyStatistics();
        transport.Start();
        Assert.True(transport.EnqueueWrite(writeRequest));
        var received = new byte[payload.Length];
        var offset = 0;
        while (offset < received.Length)
        {
            var length = await server.ReceiveAsync(received.AsMemory(offset), TestContext.Current.CancellationToken);
            Assert.NotEqual(0, length);
            offset += length;
        }

        await writeRequest.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var finalStatistics = LinuxIoUringEngine.GetZeroCopyStatistics();

        Assert.Equal(payload, received);
        Assert.True(finalStatistics.Primary > initialStatistics.Primary);
        Assert.Equal(
            finalStatistics.Primary - initialStatistics.Primary,
            finalStatistics.Notifications - initialStatistics.Notifications);
        await transport.CloseAsync(null, TestContext.Current.CancellationToken);
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

    private static bool IsIoUringTestEnabled()
        => OperatingSystem.IsLinux()
            && string.Equals(
                Environment.GetEnvironmentVariable("ORLEANS_TEST_IO_URING"),
                "1",
                StringComparison.Ordinal);

    private static async Task<(Socket Listener, Socket Client, Socket Server)> CreateSocketPair()
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await client.ConnectAsync(listener.LocalEndPoint!);
            var server = await listener.AcceptAsync();
            return (listener, client, server);
        }
        catch
        {
            client.Dispose();
            listener.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> SendFramedAsync(
        SocketMessageTransport transport,
        Socket socket,
        byte[] payload)
    {
        using var request = new FramedReadRequest(payload.Length);
        Assert.True(transport.EnqueueRead(request));
        await SendExactly(socket, CreateFrame(payload));
        return await request.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static byte[] CreateFrame(ReadOnlySpan<byte> payload)
    {
        var result = new byte[sizeof(int) * 2 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(result, payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(sizeof(int)), 0);
        payload.CopyTo(result.AsSpan(sizeof(int) * 2));
        return result;
    }

    private static async Task SendExactly(Socket socket, ReadOnlyMemory<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var length = await socket.SendAsync(buffer);
            Assert.NotEqual(0, length);
            buffer = buffer[length..];
        }
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

    private class FixedLengthReadRequest(int length) : ReadRequest, IDisposable
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

    private sealed class UnknownFramedReadRequest(int length) : FixedLengthReadRequest(length), IFramedReadRequest
    {
        public bool OnRead(ArcBufferReader bufferReader, out int framedLength)
        {
            framedLength = 0;
            return OnRead(bufferReader);
        }
    }

    private sealed class FramedReadRequest(
        int payloadLength,
        Action? frameLengthObserved = null,
        Action? beforeComplete = null) : ReadRequest, IFramedReadRequest, IDisposable
    {
        private const int FramingLength = sizeof(int) * 2;
        private readonly TaskCompletionSource<byte[]> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<byte[]> Completion => _completion.Task;

        public override bool OnRead(ArcBufferReader buffer)
        {
            var framedLength = checked(FramingLength + payloadLength);
            if (buffer.Length < framedLength)
            {
                return false;
            }

            beforeComplete?.Invoke();
            Span<byte> scratch = stackalloc byte[FramingLength];
            var lengths = buffer.Peek(in scratch);
            Assert.Equal(payloadLength, BinaryPrimitives.ReadInt32LittleEndian(lengths));
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(lengths[sizeof(int)..]));
            using var frame = buffer.ConsumeSlice(framedLength);
            var result = new byte[payloadLength];
            var copied = 0;
            var skipped = 0;
            foreach (var segment in frame)
            {
                var current = segment;
                if (skipped < FramingLength)
                {
                    var skip = Math.Min(FramingLength - skipped, current.Length);
                    current = current[skip..];
                    skipped += skip;
                }

                current.CopyTo(result.AsSpan(copied));
                copied += current.Length;
            }

            Assert.Equal(payloadLength, copied);
            _completion.TrySetResult(result);
            return true;
        }

        public override void OnError(Exception error) => _completion.TrySetException(error);

        public override void OnCanceled() => _completion.TrySetCanceled();

        public bool OnRead(ArcBufferReader bufferReader, out int framedLength)
        {
            if (bufferReader.Length < FramingLength)
            {
                framedLength = 0;
                return OnRead(bufferReader);
            }

            Span<byte> scratch = stackalloc byte[FramingLength];
            var lengths = bufferReader.Peek(in scratch);
            try
            {
                framedLength = checked(
                    FramingLength
                    + checked(
                        BinaryPrimitives.ReadInt32LittleEndian(lengths)
                        + BinaryPrimitives.ReadInt32LittleEndian(lengths[sizeof(int)..])));
                frameLengthObserved?.Invoke();
                return OnRead(bufferReader);
            }
            catch (OverflowException)
            {
                framedLength = 0;
                return OnRead(bufferReader);
            }
        }

        public void Dispose() => _completion.TrySetCanceled();
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

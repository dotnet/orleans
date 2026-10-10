using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Linq;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Internal;
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
using Orleans.Serialization.Invocation;
using Microsoft.Extensions.Time.Testing;
using TestExtensions;
using Xunit;
using SslApplicationProtocol = System.Net.Security.SslApplicationProtocol;
using SslClientAuthenticationOptions = System.Net.Security.SslClientAuthenticationOptions;
using SslStream = System.Net.Security.SslStream;

namespace Orleans.Core.Tests.Networking;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public class MessageTransportLifecycleTests
{
    [Fact]
    public void ForwardingResponse_TypedRouteSurvivesWireAndPreservesIdentityAndDeadline()
    {
        using var services = CreateServiceProvider();
        using var shared = CreateMessageHandlerShared(services);
        using var serializer = services.GetRequiredService<MessageSerializer>();
        var request = new Message
        {
            Direction = Message.Directions.Request,
            Id = new CorrelationId(123),
            TargetGrain = GrainId.Create("test", "retired"),
            SendingGrain = GrainId.Create("test", "caller"),
            SendingSilo = SiloAddress.New(IPAddress.Loopback, 30000, 1),
            TargetSilo = SiloAddress.New(IPAddress.Loopback, 30001, 1),
            TimeToLive = TimeSpan.FromMinutes(1),
            ForwardCount = 4,
            RequestContextData = new() { ["application-context"] = "must not leak" },
        };
        var destination = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        var expiry = request._timeToExpiry.GetRawTimestamp();
        var response = shared.MessageFactory.CreateForwardingResponse(request, destination);
        Assert.Equal(4, response.ForwardCount);
        Assert.Equal(expiry, response._timeToExpiry.GetRawTimestamp());
        Assert.Null(response.RequestContextData);
        var remaining = response.TimeToLive;
        using var buffer = new ArcBufferWriter();
        var (headerLength, bodyLength) = serializer.Write(buffer, response);
        var read = new MessageReadRequest(shared);
        read.Headers = buffer.ConsumeSlice(headerLength);
        read.Body = buffer.ConsumeSlice(bodyLength);
        serializer.ReadHeaders(read, out var decoded);
        serializer.ReadBodyObject(decoded, read);
        var status = Assert.IsType<StatusResponse>(decoded.BodyObject);
        Assert.True(status.IsRouteUpdate);
        Assert.Equal(destination, status.ForwardedTo);
        Assert.Equal(4, decoded.ForwardCount);
        Assert.False(status.IsExecuting);
        Assert.False(status.IsWaiting);
        Assert.Equal(request.Id, decoded.Id);
        Assert.Equal(request.SendingGrain, decoded.TargetGrain);
        Assert.Equal(request.TargetGrain, decoded.SendingGrain);
        Assert.Equal(request.SendingSilo, decoded.TargetSilo);
        Assert.Equal(request.TargetSilo, decoded.SendingSilo);
        Assert.Equal(Message.Directions.Response, decoded.Direction);
        Assert.Equal(Message.ResponseTypes.Status, decoded.Result);
        Assert.Null(decoded.RequestContextData);
        Assert.True(decoded.TimeToLive <= remaining);
        Assert.True(decoded.TimeToLive > TimeSpan.Zero);
        read.Reset();
        decoded.Dispose();
        request.Dispose();
        response.Dispose();
    }

    [Fact]
    public void ConnectionPreamble_PreservesLegacyPeerFieldsWithoutCapabilityNegotiation()
    {
        using var services = CreateServiceProvider();
        var current = services.GetRequiredService<Serializer<ConnectionPreamble>>();
        var legacy = services.GetRequiredService<Serializer<LegacyConnectionPreamble>>();
        var address = SiloAddress.New(IPAddress.Loopback, 30000, 1);
        var oldPreamble = new LegacyConnectionPreamble
        {
            SiloAddress = address,
            ClusterId = "cluster",
            NodeIdentity = GrainId.Create("test", "silo"),
        };
        var decodedCurrent = current.Deserialize(legacy.SerializeToArray(oldPreamble));
        Assert.NotNull(decodedCurrent);
        Assert.Equal(address, decodedCurrent.SiloAddress);
        Assert.Equal(oldPreamble.NodeIdentity, decodedCurrent.NodeIdentity);
        var newPreamble = new ConnectionPreamble
        {
            SiloAddress = address,
            ClusterId = "cluster",
            NodeIdentity = oldPreamble.NodeIdentity,
        };
        var decodedLegacy = legacy.Deserialize(current.SerializeToArray(newPreamble));
        Assert.NotNull(decodedLegacy);
        Assert.Equal(address, decodedLegacy.SiloAddress);
        Assert.Equal("cluster", decodedLegacy.ClusterId);
        Assert.Equal(oldPreamble.NodeIdentity, decodedLegacy.NodeIdentity);
        var roundTrip = current.Deserialize(current.SerializeToArray(newPreamble));
        Assert.NotNull(roundTrip);
        Assert.Equal(address, roundTrip.SiloAddress);
        Assert.Equal(oldPreamble.NodeIdentity, roundTrip.NodeIdentity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    public void ForwardingStatus_IsAdditiveAndLegacyPeersMayDropAdvisoryRoute(int generation)
    {
        var fieldIds = typeof(StatusResponse).GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(member => member.GetCustomAttributesData()
                .Where(attribute => attribute.AttributeType == typeof(IdAttribute))
                .Select(attribute => Convert.ToUInt32(attribute.ConstructorArguments[0].Value)))
            .OrderBy(id => id);
        Assert.Equal(new uint[] { 0, 1, 2 }, fieldIds);
        using var services = CreateServiceProvider();
        using var shared = CreateMessageHandlerShared(services);
        using var messageSerializer = services.GetRequiredService<MessageSerializer>();
        var current = services.GetRequiredService<Serializer<StatusResponse>>();
        var legacy = services.GetRequiredService<Serializer<LegacyStatusResponse>>();
        var destination = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        var status = new StatusResponse(false, true, ["waiting diagnostic"])
        {
            ForwardedTo = destination,
        };
        var decodedLegacy = legacy.Deserialize(current.SerializeToArray(status))!;
        Assert.Equal(2u, decodedLegacy.Flags);
        Assert.Equal(new[] { "waiting diagnostic" }, decodedLegacy.Diagnostics);
        var decodedModern = current.Deserialize(current.SerializeToArray(status))!;
        Assert.Equal(destination, decodedModern.ForwardedTo);
        Assert.True(decodedModern.IsRouteUpdate);
        Assert.True(decodedModern.IsWaiting);
        Assert.False(decodedModern.IsExecuting);
        Assert.Equal(new[] { "waiting diagnostic" }, decodedModern.Diagnostics);
        var legacyRelay = current.Deserialize(legacy.SerializeToArray(decodedLegacy))!;
        Assert.Null(legacyRelay.ForwardedTo);
        Assert.False(legacyRelay.IsRouteUpdate);
        Assert.True(legacyRelay.IsWaiting);
        Assert.False(legacyRelay.IsExecuting);
        Assert.Equal(new[] { "waiting diagnostic" }, legacyRelay.Diagnostics);

        // A released peer can relay only the original status body fields. Generation
        // stays in the existing packed header, not an additional serialized body field.
        using var response = new Message
        {
            Direction = Message.Directions.Response,
            Result = Message.ResponseTypes.Status,
            Id = new CorrelationId(123),
            SendingGrain = GrainId.Create("test", "retired"),
            TargetGrain = GrainId.Create("test", "caller"),
            ForwardCount = generation,
            BodyObject = decodedModern,
        };
        using (var decoded = RoundTrip(response))
        {
            Assert.Equal(destination, Assert.IsType<StatusResponse>(decoded.BodyObject).ForwardedTo);
        }

        response.BodyObject = legacyRelay;
        using (var decoded = RoundTrip(response))
        {
            var relayedStatus = Assert.IsType<StatusResponse>(decoded.BodyObject);
            Assert.False(relayedStatus.IsRouteUpdate);
            Assert.Null(relayedStatus.ForwardedTo);
            Assert.True(relayedStatus.IsWaiting);
            Assert.Equal(new[] { "waiting diagnostic" }, relayedStatus.Diagnostics);
        }

        Message RoundTrip(Message original)
        {
            using var buffer = new ArcBufferWriter();
            var (headerLength, bodyLength) = messageSerializer.Write(buffer, original);
            var read = new MessageReadRequest(shared)
            {
                Headers = buffer.ConsumeSlice(headerLength),
                Body = buffer.ConsumeSlice(bodyLength),
            };
            messageSerializer.ReadHeaders(read, out var decoded);
            messageSerializer.ReadBodyObject(decoded, read);
            read.Reset();
            Assert.Equal(generation, decoded.ForwardCount);
            Assert.Equal(original.Id, decoded.Id);
            Assert.Equal(original.SendingGrain, decoded.SendingGrain);
            Assert.Equal(original.TargetGrain, decoded.TargetGrain);
            Assert.Equal(Message.Directions.Response, decoded.Direction);
            Assert.Equal(Message.ResponseTypes.Status, decoded.Result);
            return decoded;
        }
    }

    [Fact]
    public void LegacyStatus_DiagnosticsAreNotRouteUpdates()
    {
        using var services = CreateServiceProvider();
        var current = services.GetRequiredService<Serializer<StatusResponse>>();
        var legacy = services.GetRequiredService<Serializer<LegacyStatusResponse>>();
        var original = new StatusResponse(true, false, ["executing diagnostic"]);
        var decoded = current.Deserialize(legacy.SerializeToArray(new LegacyStatusResponse
        {
            Flags = 1,
            Diagnostics = original.Diagnostics,
        }))!;
        Assert.True(decoded.IsExecuting);
        Assert.False(decoded.IsWaiting);
        Assert.False(decoded.IsRouteUpdate);
        Assert.Null(decoded.ForwardedTo);
        Assert.Equal(new[] { "executing diagnostic" }, decoded.Diagnostics);
    }

    [Fact]
    public void RejectionResponse_PreservesOriginalThreeFieldWireSchema()
    {
        var fieldIds = typeof(RejectionResponse).GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(member => member.GetCustomAttributesData()
                .Where(attribute => attribute.AttributeType == typeof(IdAttribute))
                .Select(attribute => Convert.ToUInt32(attribute.ConstructorArguments[0].Value)))
            .OrderBy(id => id);
        Assert.Equal(new uint[] { 0, 1, 2 }, fieldIds);
        using var services = CreateServiceProvider();
        var current = services.GetRequiredService<Serializer<RejectionResponse>>();
        var legacy = services.GetRequiredService<Serializer<LegacyRejectionResponse>>();
        var response = new RejectionResponse
        {
            RejectionInfo = "accepted transport write failed",
            RejectionType = Message.RejectionTypes.Transient,
            Exception = new InvalidOperationException("transport sentinel"),
        };
        var bytes = current.SerializeToArray(response);
        var decoded = current.Deserialize(bytes)!;
        Assert.Equal("accepted transport write failed", decoded.RejectionInfo);
        Assert.Equal(Message.RejectionTypes.Transient, decoded.RejectionType);
        Assert.Equal("transport sentinel", Assert.IsType<InvalidOperationException>(decoded.Exception).Message);
        var decodedLegacy = legacy.Deserialize(bytes)!;
        Assert.Equal("accepted transport write failed", decodedLegacy.RejectionInfo);
        Assert.Equal(Message.RejectionTypes.Transient, decodedLegacy.RejectionType);
        Assert.Equal("transport sentinel", Assert.IsType<InvalidOperationException>(decodedLegacy.Exception).Message);
        var fromLegacy = current.Deserialize(legacy.SerializeToArray(decodedLegacy))!;
        Assert.Equal("accepted transport write failed", fromLegacy.RejectionInfo);
        Assert.Equal(Message.RejectionTypes.Transient, fromLegacy.RejectionType);
    }

    [GenerateSerializer]
    internal sealed class LegacyRejectionResponse
    {
        [Id(0)] public string RejectionInfo { get; set; } = "";
        [Id(1)] public Message.RejectionTypes RejectionType { get; set; }
        [Id(2)] public Exception? Exception { get; set; }
    }

    [GenerateSerializer]
    public sealed class LegacyStatusResponse
    {
        [Id(0)] public uint Flags { get; set; }
        [Id(1)] public List<string> Diagnostics { get; set; } = [];
    }

    [GenerateSerializer]
    public sealed class LegacyConnectionPreamble
    {
        [Id(0)] public NetworkProtocolVersion NetworkProtocolVersion { get; init; }
        [Id(1)] public GrainId NodeIdentity { get; init; }
        [Id(2)] public SiloAddress? SiloAddress { get; init; }
        [Id(3)] public string ClusterId { get; init; } = null!;
    }

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
        Assert.Equal(request._timeToExpiry.GetRawTimestamp(), response._timeToExpiry.GetRawTimestamp());
        var remaining = response.TimeToLive;
        using var buffer = new ArcBufferWriter();
        var (headerLength, _) = serializer.Write(buffer, response);
        var readRequest = new MessageReadRequest(shared);
        readRequest.Headers = buffer.ConsumeSlice(headerLength);
        typeof(MessageReadRequest).GetField("_headerLength", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(readRequest, headerLength);

        serializer.ReadHeaders(readRequest, out var deserialized);

        Assert.NotNull(deserialized.TimeToLive);
        Assert.True(deserialized.TimeToLive > TimeSpan.Zero);
        Assert.True(deserialized.TimeToLive <= remaining);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MessageWriteRequest_SuccessClearsBufferedAndLocalBodiesWithoutDoubleReturn(bool buffered)
    {
        using var services = CreateServiceProvider();
        using var shared = CreateMessageHandlerShared(services);
        var transport = new CapturingTransport();
        var connection = new RetryTrackingConnection(transport, CreateConnectionCommon(services, shared), shared.MessageCenter);
        using var bodyWriter = new ArcBufferWriter();
        var read = shared.GetReceiveMessageHandler();
        read._originalResponseType = Message.ResponseTypes.Success;
        bodyWriter.Write([1, 2, 3]);
        read.Body = bodyWriter.ConsumeSlice(3);
        typeof(MessageReadRequest).GetField("_bodyLength", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(read, 3);
        using var message = new Message
        {
            Direction = Message.Directions.Response,
            Result = Message.ResponseTypes.Success,
            BodyObject = buffered ? read : Response.FromResult(173),
        };
        if (!buffered) read.Reset();
        var write = shared.GetSendMessageHandler(connection);
        write.WriteMessage(message, default);
        write.CompleteWriting();
        Assert.NotNull(message._bodyObject);
        Assert.True(write.Length > Message.LENGTH_HEADER_SIZE);
        write.SetResult();
        Assert.Null(message._bodyObject);
        Assert.Equal(0, read.BodyLength);
        Assert.Equal(0, read.Body.Length);
        Assert.Equal(1, connection.SentMessageCount);
        var first = shared.GetReceiveMessageHandler();
        Assert.Same(read, first);
        message.ReleaseBodyBuffer();
        message.Dispose();
        var second = shared.GetReceiveMessageHandler();
        Assert.NotSame(first, second);
        first.Reset();
        second.Reset();
    }

    [Theory]
    [InlineData((int)Message.Directions.Request, (int)Message.ResponseTypes.None)]
    [InlineData((int)Message.Directions.Response, (int)Message.ResponseTypes.Success)]
    public void Message_FormattingFailure_PreservesLazyBodyAndResponseType(int directionValue, int responseTypeValue)
    {
        var direction = (Message.Directions)directionValue;
        var responseType = (Message.ResponseTypes)responseTypeValue;
        using var serviceProvider = CreateServiceProvider();
        using var shared = CreateMessageHandlerShared(serviceProvider);
        using var bodyWriter = new ArcBufferWriter();
        bodyWriter.Write([0xff]);
        var readRequest = new MessageReadRequest(shared);
        readRequest._originalResponseType = responseType;
        readRequest.Body = bodyWriter.ConsumeSlice(1);
        typeof(MessageReadRequest).GetField("_bodyLength", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(readRequest, 1);
        using var message = new Message { Direction = direction, Result = responseType };
        message.SetMessageReadRequest(readRequest);

        Assert.Contains("Unable to deserialize message body:", message.ToString());
        Assert.Contains("Unable to deserialize message body:", message.ToString());
        Assert.Same(readRequest, message._bodyObject);
        Assert.Equal(responseType, message.Result);
        Assert.Equal([0xff], readRequest.Body.ToArray());

        using var serializer = new MessageSerializer(serviceProvider.GetRequiredService<SerializerSessionPool>(), new SiloMessagingOptions());
        using var output = new ArcBufferWriter();
        var lengths = serializer.Write(output, message);
        Assert.Equal(1, lengths.BodyLength);

        if (direction == Message.Directions.Request)
        {
            Assert.ThrowsAny<Exception>(() => _ = message.BodyObject);
        }
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
    public async Task ConnectionCommon_ConcurrentHandlerAcquisitionResolvesOnce()
    {
        using var services = CreateServiceProvider();
        using var shared = CreateMessageHandlerShared(services);
        var common = CreateConnectionCommon(services, shared);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquisitions = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return common.MessageHandlerShared;
        }, TestContext.Current.CancellationToken)).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(acquisitions).WaitAsync(TestContext.Current.CancellationToken);

        Assert.All(results, result => Assert.Same(shared, result));
        Assert.Same(shared, common.MessageHandlerShared);
        common.ServiceProvider.Received(1).GetService(typeof(MessageHandlerShared));
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

    [Theory]
    [InlineData("ordinary")]
    [InlineData("grain-response")]
    [InlineData("client-response")]
    public async Task RetirementDrain_WaitsForQueuedSerializationAndAcceptedWriteCompletion(string traffic)
    {
        using var services = CreateServiceProvider();
        using var shared = CreateMessageHandlerShared(services);
        await using var transport = new CancelableTransport();
        using var connection = new BlockingSendConnection(transport, CreateConnectionCommon(services, shared),
            shared.MessageCenter, TestContext.Current.CancellationToken);
        var silo = SiloAddress.New(IPAddress.Loopback, 30000, 1);
        using var message = new Message
        {
            Direction = traffic == "ordinary" ? Message.Directions.Request : Message.Directions.Response,
            BodyObject = "retirement",
            IsSystemMessage = traffic != "ordinary",
            SendingGrain = traffic == "client-response" ? GrainId.Create("sys.client", "observer") : GrainId.Create("test", "origin"),
            TargetGrain = traffic == "ordinary" ? GrainId.Create("test", "target")
                : SystemTargetGrainId.Create(GrainType.Create("sys.svc.boundary"), silo).GrainId,
        };
        Assert.True(message.RequiresApplicationDrain);
        if (traffic != "ordinary") Assert.True(message.TargetGrain.IsSystemTarget());
        connection.Send(message);
        await connection.PrepareEntered.WaitAsync(TestContext.Current.CancellationToken);
        var drain = connection.DrainAsync();
        Assert.False(drain.IsCompleted);
        connection.ReleaseSend();
        await transport.WriteStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(drain.IsCompleted);
        transport.CompleteWrite();
        await drain.WaitAsync(TestContext.Current.CancellationToken);
        using var late = new Message
        {
            Direction = message.Direction,
            IsSystemMessage = message.IsSystemMessage,
            SendingGrain = message.SendingGrain,
            TargetGrain = message.TargetGrain,
            BodyObject = "late",
        };
        connection.Send(late);
        Assert.Null(late.BodyObject);
        Assert.Equal(1, transport.WriteCount);
    }

    [Fact]
    public async Task RetirementDrain_FailedAcceptedGrainWriteDoesNotReplayOrReject()
    {
        using var services = CreateServiceProvider();
        using var shared = CreateMessageHandlerShared(services);
        await using var transport = new CancelableTransport();
        var connection = new RetryTrackingConnection(transport, CreateConnectionCommon(services, shared), shared.MessageCenter);
        using var message = new Message
        {
            Direction = Message.Directions.Request,
            TargetGrain = GrainId.Create("test", "target"),
            BodyObject = "retirement",
        };
        Assert.True(message.IsRelocatableRequest);
        connection.Send(message);
        await transport.WriteStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var drain = connection.DrainAsync();
        Assert.False(drain.IsCompleted);
        await transport.CloseAsync(new ConnectionClosedException(), TestContext.Current.CancellationToken);
        await drain.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, connection.ApplicationWriteFailures);
        Assert.Equal(0, connection.SentMessageCount);
        Assert.False(connection.Retries.Reader.TryRead(out _));
        shared.MessageCenter.DidNotReceive().DispatchLocalMessage(Arg.Any<Message>());
        Assert.Null(message.BodyObject);
        Assert.Equal(0, message.RetryCount);
        Assert.Equal(1, transport.WriteCount);
    }

    [Theory]
    [InlineData((int)Message.Directions.Response, "grain", true, true)]
    [InlineData((int)Message.Directions.Response, "client", true, true)]
    [InlineData((int)Message.Directions.Response, "system", true, false)]
    [InlineData((int)Message.Directions.Response, "default", true, false)]
    [InlineData((int)Message.Directions.Request, "grain", true, false)]
    [InlineData((int)Message.Directions.OneWay, "client", true, false)]
    [InlineData((int)Message.Directions.Response, "system", false, true)]
    [InlineData((int)Message.Directions.Request, "default", false, true)]
    public void Message_ApplicationDrainClassificationUsesResponseOrigin(
        int direction, string origin, bool systemMessage, bool expected)
    {
        var silo = SiloAddress.New(IPAddress.Loopback, 30000, 1);
        using var message = new Message
        {
            Direction = (Message.Directions)direction,
            IsSystemMessage = systemMessage,
            SendingGrain = origin switch
            {
                "grain" => GrainId.Create("test", "origin"),
                "client" => GrainId.Create("sys.client", "observer"),
                "system" => SystemTargetGrainId.Create(GrainType.Create("sys.svc.boundary"), silo).GrainId,
                _ => default,
            },
            TargetGrain = SystemTargetGrainId.Create(GrainType.Create("sys.svc.boundary"), silo).GrainId,
        };
        Assert.True(message.TargetGrain.IsSystemTarget());
        Assert.Equal(origin == "client", message.SendingGrain.IsClient());
        Assert.Equal(origin == "system", message.SendingGrain.IsSystemTarget());
        Assert.Equal(expected, message.RequiresApplicationDrain);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MessageWriteRequest_AcceptedGrainFailurePreservesCallbackAndReleasesBodyOnce(
        bool buffered, bool expireDeadline)
    {
        using var services = CreateServiceProvider();
        using var shared = CreateMessageHandlerShared(services);
        using var failed = new MetricCollector<int>(services.GetRequiredService<IMeterFactory>(), "Microsoft.Orleans", InstrumentNames.MESSAGING_SENT_FAILED);
        await using var transport = new CancelableTransport();
        var connection = new RetryTrackingConnection(transport, CreateConnectionCommon(services, shared), shared.MessageCenter);
        var clock = new FakeTimeProvider();
        var completion = new BoundaryCompletionSource();
        var unregisters = 0;
        using var message = new Message
        {
            Direction = Message.Directions.Request,
            Id = new CorrelationId(173),
            SendingGrain = GrainId.Create("test", "caller"),
            TargetGrain = GrainId.Create("test", "target"),
            TargetSilo = SiloAddress.New(IPAddress.Loopback, 30000, 1),
            BodyObject = new Tester.CallbackDataTests.CancellableTestInvokable(),
            TimeToLive = TimeSpan.FromSeconds(7),
        };
        var callback = new CallbackData(new SharedCallbackData(_ => unregisters++, NullLogger<CallbackData>.Instance,
            clock, TimeSpan.FromMinutes(1), false, false, null), completion, message,
            new ApplicationRequestInstruments(services.GetRequiredService<OrleansInstruments>()));
        var expiry = message._timeToExpiry.GetRawTimestamp();
        MessageReadRequest? read = null;
        using var encoded = new ArcBufferWriter();
        if (buffered)
        {
            using var serializer = services.GetRequiredService<MessageSerializer>();
            var (headerLength, bodyLength) = serializer.Write(encoded, message);
            using var headers = encoded.ConsumeSlice(headerLength);
            read = shared.GetReceiveMessageHandler();
            read.Body = encoded.ConsumeSlice(bodyLength);
            typeof(MessageReadRequest).GetField("_bodyLength", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(read, bodyLength);
            message.SetMessageReadRequest(read);
        }
        var write = shared.GetSendMessageHandler(connection);
        write.WriteMessage(message, default);
        write.CompleteWriting();
        Assert.True(transport.EnqueueWrite(write));
        Assert.Same(message, write.GetMessage(0));
        await transport.CloseAsync(new ConnectionClosedException(), TestContext.Current.CancellationToken);
        // Failure preceded the local drain: it must not poison a later connection drain.
        await connection.DrainAsync().WaitAsync(TestContext.Current.CancellationToken);
        Assert.Same(message, callback.Message);
        Assert.False(callback.IsCompleted);
        Assert.Null(completion.Response);
        Assert.Equal(0, unregisters);
        Assert.Null(message._bodyObject);
        Assert.Equal(expiry, message._timeToExpiry.GetRawTimestamp());
        Assert.Equal(new CorrelationId(173), message.Id);
        Assert.Equal(GrainId.Create("test", "caller"), message.SendingGrain);
        Assert.Equal(GrainId.Create("test", "target"), message.TargetGrain);
        Assert.Equal(0, message.RetryCount);
        Assert.False(connection.Retries.Reader.TryRead(out _));
        shared.MessageCenter.DidNotReceive().DispatchLocalMessage(Arg.Any<Message>());
        Assert.Equal(1, Assert.Single(failed.GetMeasurementSnapshot()).Value);
        if (read is not null)
        {
            Assert.Equal(0, read.BodyLength);
            Assert.Equal(0, read.Body.Length);
            var first = shared.GetReceiveMessageHandler();
            Assert.Same(read, first);
            message.Dispose();
            var second = shared.GetReceiveMessageHandler();
            Assert.NotSame(first, second);
            first.Reset();
            second.Reset();
        }
        if (expireDeadline)
        {
            clock.Advance(TimeSpan.FromSeconds(7));
            Assert.False(callback.IsExpired(clock.GetTimestamp()));
            clock.Advance(TimeSpan.FromTicks(1));
            Assert.True(callback.IsExpired(clock.GetTimestamp()));
            callback.OnTimeout();
            Assert.Contains("Response did not arrive on time", Assert.IsType<TimeoutException>(completion.Response!.Exception).Message);
        }
        else
        {
            callback.DoCallback(new Message { BodyObject = Response.FromResult(174) });
            Assert.Equal(174, completion.Response!.GetResult<int>());
        }
        Assert.True(callback.IsCompleted);
        Assert.Equal(1, completion.CompletionCount);
        // Real responses are unregistered by the runtime before DoCallback; timeout
        // unregisters from CallbackData itself.
        Assert.Equal(expireDeadline ? 1 : 0, unregisters);
        Assert.Equal(1, transport.WriteCount);
    }

    [Theory]
    [InlineData("response")]
    [InlineData("system")]
    [InlineData("pinned")]
    public async Task MessageWriteRequest_AcceptedNonrelocatableFailurePreservesReroute(string traffic)
    {
        using var services = CreateServiceProvider();
        using var shared = CreateMessageHandlerShared(services);
        await using var transport = new CancelableTransport();
        var connection = new RetryTrackingConnection(transport, CreateConnectionCommon(services, shared), shared.MessageCenter);
        using var message = new Message
        {
            Direction = traffic == "response" ? Message.Directions.Response : Message.Directions.Request,
            TargetGrain = GrainId.Create("test", "target"),
            IsSystemMessage = traffic == "system",
            IsLocalOnly = traffic == "pinned",
            BodyObject = "nonrelocatable",
        };
        Assert.False(message.IsRelocatableRequest);
        var write = shared.GetSendMessageHandler(connection);
        write.WriteMessage(message, default);
        write.CompleteWriting();
        Assert.True(transport.EnqueueWrite(write));
        var error = new ConnectionClosedException();
        await transport.CloseAsync(error, TestContext.Current.CancellationToken);
        var retry = await connection.Retries.Reader.ReadAsync(TestContext.Current.CancellationToken);
        await connection.DrainAsync().WaitAsync(TestContext.Current.CancellationToken);
        Assert.Same(message, retry.Message);
        Assert.Same(error, retry.Error);
        Assert.Equal("nonrelocatable", message.BodyObject);
        Assert.False(connection.Retries.Reader.TryRead(out _));
        Assert.Equal(1, transport.WriteCount);
        Assert.Equal(0, connection.SentMessageCount);
    }

    [Fact]
    public async Task RetirementDrain_WaitsForDecodedRequestsAcrossConnections()
    {
        using var services = CreateServiceProvider();
        using var shared = CreateMessageHandlerShared(services);
        using var serializer = services.GetRequiredService<MessageSerializer>();
        await using var firstTransport = new CancelableTransport();
        await using var secondTransport = new CancelableTransport();
        using var first = new BlockingReceiveConnection(firstTransport, CreateConnectionCommon(services, shared),
            shared.MessageCenter, TestContext.Current.CancellationToken);
        using var second = new BlockingReceiveConnection(secondTransport, CreateConnectionCommon(services, shared),
            shared.MessageCenter, TestContext.Current.CancellationToken);
        ReadFrame(first, new CorrelationId(123));
        ReadFrame(second, new CorrelationId(456));
        await Task.WhenAll(first.Decoded, second.Decoded).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var drain = Task.WhenAll(first.DrainIncomingApplicationDispatchAsync(), second.DrainIncomingApplicationDispatchAsync());
        Assert.False(drain.IsCompleted);
        first.Release();
        Assert.False(drain.IsCompleted);
        second.Release();
        await drain.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(new CorrelationId(123), first.ReceivedId);
        Assert.Equal(new CorrelationId(456), second.ReceivedId);
        Assert.Equal(1, first.DispatchCount);
        Assert.Equal(1, second.DispatchCount);
        await Task.WhenAll(first.CloseAsync(null), second.CloseAsync(null)).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        void ReadFrame(Connection connection, CorrelationId id)
        {
            using var frame = new ArcBufferWriter();
            frame.AdvanceWriter(Message.LENGTH_HEADER_SIZE);
            using var message = new Message
            {
                Direction = Message.Directions.Request,
                TargetGrain = GrainId.Create("test", "target"),
                Id = id,
                BodyObject = "payload",
            };
            var (headerLength, bodyLength) = serializer.Write(frame, message);
            Span<byte> framing = stackalloc byte[Message.LENGTH_HEADER_SIZE];
            BinaryPrimitives.WriteInt32LittleEndian(framing, headerLength);
            BinaryPrimitives.WriteInt32LittleEndian(framing[sizeof(int)..], bodyLength);
            frame.WriteAt(0, framing);
            var read = shared.GetReceiveMessageHandler();
            read.SetConnection(connection);
            Assert.True(read.OnRead(new ArcBufferReader(frame)));
        }
    }

    [Fact]
    public async Task RetirementDrain_WaitsForQueuedFrameAfterConnectionCloses()
    {
        using var services = CreateServiceProvider();
        using var shared = CreateMessageHandlerShared(services);
        using var serializer = services.GetRequiredService<MessageSerializer>();
        await using var transport = new TrackingTransport();
        var connection = new RecordingReceiveConnection(transport, CreateConnectionCommon(services, shared), shared.MessageCenter);
        using var message = new Message
        {
            Direction = Message.Directions.Request,
            TargetGrain = GrainId.Create("test", "target"),
            Id = new CorrelationId(173),
            BodyObject = "admitted frame",
        };
        using var frame = new ArcBufferWriter();
        frame.AdvanceWriter(Message.LENGTH_HEADER_SIZE);
        var (headerLength, bodyLength) = serializer.Write(frame, message);
        var framing = new byte[Message.LENGTH_HEADER_SIZE];
        BinaryPrimitives.WriteInt32LittleEndian(framing.AsSpan(), headerLength);
        BinaryPrimitives.WriteInt32LittleEndian(framing.AsSpan(sizeof(int)), bodyLength);
        frame.WriteAt(0, framing);
        var poolLock = typeof(MessageHandlerShared).GetField("_poolLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shared)!;
        Task close;
        Task drain;
        // Hold serializer acquisition on the queued decoder, while OnRead can rent
        // and return its frame validator under the same reentrant lock.
        lock (poolLock)
        {
            var read = shared.GetReceiveMessageHandler();
            read.SetConnection(connection);
            Assert.True(read.OnRead(new ArcBufferReader(frame)));
            close = connection.CloseAsync(null);
            drain = connection.DrainIncomingApplicationDispatchAsync();
            Assert.False(drain.IsCompleted);
            Assert.Equal(0, connection.DispatchCount);
        }

        await Task.WhenAll(close, drain).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, connection.DispatchCount);
        Assert.Equal(1, connection.RecordCount);
        Assert.Equal(message.Id, connection.ReceivedId);
    }

    [Fact]
    public async Task MessageReadRequest_ClosedDispatchAdmissionDropsOnlyOrdinaryRequests()
    {
        using var services = CreateServiceProvider();
        using var shared = CreateMessageHandlerShared(services);
        using var serializer = services.GetRequiredService<MessageSerializer>();
        await using var transport = new TrackingTransport();
        var connection = new RecordingReceiveConnection(transport, CreateConnectionCommon(services, shared), shared.MessageCenter);
        await connection.DrainIncomingApplicationDispatchAsync();

        foreach (var direction in new[] { Message.Directions.Request, Message.Directions.OneWay, Message.Directions.Response })
            foreach (var systemMessage in new[] { false, true })
            {
                using var message = new Message
                {
                    Direction = direction,
                    IsSystemMessage = systemMessage,
                    Id = new CorrelationId(123),
                    TargetGrain = GrainId.Create("test", "target"),
                    SendingGrain = GrainId.Create("test", "caller"),
                };
                using var buffer = new ArcBufferWriter();
                var (headerLength, bodyLength) = serializer.Write(buffer, message);
                Assert.Equal(0, bodyLength);
                var read = shared.GetReceiveMessageHandler();
                read.SetConnection(connection);
                read.Headers = buffer.ConsumeSlice(headerLength);
                var receivedBefore = connection.DispatchCount;
                var recordedBefore = connection.RecordCount;

                ((IThreadPoolWorkItem)read).Execute();

                Assert.Equal(recordedBefore + 1, connection.RecordCount);
                Assert.Equal(message.Id, connection.RecordedId);
                Assert.Equal(direction, connection.RecordedDirection);
                Assert.Equal(systemMessage, connection.RecordedSystemMessage);
                var dispatched = systemMessage || direction == Message.Directions.Response;
                Assert.Equal(receivedBefore + (dispatched ? 1 : 0), connection.DispatchCount);
                if (dispatched) Assert.Equal(message.Id, connection.ReceivedId);
                var reused = shared.GetReceiveMessageHandler();
                Assert.Same(read, reused);
                Assert.Equal(0, reused.Headers.Length);
                Assert.Equal(0, reused.Body.Length);
                reused.Reset();
            }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementDrain_WaitsForQueuedRerouteProducer(bool acceptedWrite)
    {
        using var services = CreateServiceProvider();
        using var shared = CreateMessageHandlerShared(services);
        await using var transport = new CancelableTransport();
        using var connection = new BlockingRerouteConnection(transport, CreateConnectionCommon(services, shared),
            shared.MessageCenter, TestContext.Current.CancellationToken);
        using var message = new Message
        {
            Direction = Message.Directions.Response,
            TargetGrain = GrainId.Create("test", "target"),
            BodyObject = "pending disposition",
        };
        Task drain;
        if (acceptedWrite)
        {
            connection.Send(message);
            await transport.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            drain = connection.DrainAsync();
            Assert.False(drain.IsCompleted);
            await transport.CloseAsync(new ConnectionClosedException(), TestContext.Current.CancellationToken);
        }
        else
        {
            connection.RerouteMessage(message);
            drain = connection.DrainAsync();
        }

        await connection.Entered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(drain.IsCompleted);
        connection.Release();
        await drain.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Null(message.BodyObject);
        Assert.Equal(1, connection.RerouteCount);
    }

    [Fact]
    public async Task RetirementDrain_PreservesSystemTrafficAfterApplicationSeal()
    {
        using var services = CreateServiceProvider();
        using var shared = CreateMessageHandlerShared(services);
        await using var transport = new CapturingTransport();
        var connection = new RetryTrackingConnection(transport, CreateConnectionCommon(services, shared), shared.MessageCenter);
        await connection.DrainAsync();
        var silo = SiloAddress.New(IPAddress.Loopback, 30000, 1);
        using var message = new Message
        {
            Direction = Message.Directions.Response,
            IsSystemMessage = true,
            SendingGrain = SystemTargetGrainId.Create(Constants.CatalogType, silo).GrainId,
            TargetGrain = SystemTargetGrainId.Create(Constants.CatalogType, silo).GrainId,
            BodyObject = "membership",
        };
        Assert.False(message.RequiresApplicationDrain);
        connection.Send(message);
        var bytes = await transport.Writes.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(bytes.Length > Message.LENGTH_HEADER_SIZE);
        Assert.Equal(1, connection.SentMessageCount);
        Assert.Null(message.BodyObject);
    }

    [Fact]
    public async Task UnacceptedWriteRetryPreservesOriginalRequestAndDeadline()
    {
        using var services = CreateServiceProvider();
        using var shared = CreateMessageHandlerShared(services);
        await using var rejectedTransport = new RejectingWriteTransport();
        await using var replacementTransport = new CapturingTransport();
        using var replacement = new SignalingSendConnection(replacementTransport, CreateConnectionCommon(services, shared), shared.MessageCenter);
        using var source = new HeldRetryConnection(rejectedTransport, CreateConnectionCommon(services, shared),
            shared.MessageCenter, replacement, TestContext.Current.CancellationToken);
        using var message = new Message
        {
            Direction = Message.Directions.Request,
            SendingGrain = GrainId.Create("test", "caller"),
            TargetGrain = GrainId.Create("test", "target"),
            TargetSilo = SiloAddress.New(IPAddress.Loopback, 30000, 1),
            Id = new CorrelationId(123),
            BodyObject = "original payload",
            TimeToLive = TimeSpan.FromMinutes(1),
        };
        var expiry = message._timeToExpiry.GetRawTimestamp();
        var outboundWork = new AdmissionGate();
        var sendAdmission = outboundWork.TryEnter();
        source.Send(message, ref sendAdmission);
        Assert.False(sendAdmission.Entered);
        await source.RetryEntered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var drain = outboundWork.CloseAsync();
        Assert.False(drain.IsCompleted);
        Assert.Equal("original payload", message.BodyObject);
        source.Release();
        await replacement.PrepareEntered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var bytes = await replacementTransport.Writes.Reader.ReadAsync(TestContext.Current.CancellationToken);
        await replacement.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await drain.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(bytes.Length > Message.LENGTH_HEADER_SIZE);
        Assert.Equal(new CorrelationId(123), message.Id);
        Assert.Equal(GrainId.Create("test", "target"), message.TargetGrain);
        Assert.Equal(expiry, message._timeToExpiry.GetRawTimestamp());
        Assert.Equal(0, message.ForwardCount);
        Assert.Equal(1, rejectedTransport.Attempts);
        Assert.Null(message.BodyObject);
        await source.CloseAsync(null).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(nameof(ConnectionClosedException), LogLevel.Information)]
    [InlineData(nameof(ConnectionAbortedException), LogLevel.Error)]
    [InlineData(nameof(OperationCanceledException), LogLevel.Error)]
    [InlineData(nameof(InvalidOperationException), LogLevel.Error)]
    public async Task MessageWriteRequest_TransportCloseDropsAcceptedGrainRequestAndReroutesResponse(string exceptionType, LogLevel expectedLogLevel)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var serviceProvider = CreateServiceProvider();
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger("Orleans.Connections").Returns(logger);
        using var shared = CreateMessageHandlerShared(serviceProvider, loggerFactory);
        await using var transport = new CancelableTransport();
        var connection = new RetryTrackingConnection(transport, CreateConnectionCommon(serviceProvider, shared), shared.MessageCenter);
        using var first = new Message { Direction = Message.Directions.Request, TargetGrain = GrainId.Create("test", "target"), BodyObject = new byte[] { 1 } };
        using var second = new Message { Direction = Message.Directions.Response, BodyObject = new byte[] { 2 } };
        var request = shared.GetSendMessageHandler(connection);
        request.WriteMessage(first, default);
        request.WriteMessage(second, default);
        request.CompleteWriting();
        Assert.True(transport.EnqueueWrite(request));
        Exception error = exceptionType switch
        {
            nameof(ConnectionClosedException) => new ConnectionClosedException(),
            nameof(ConnectionAbortedException) => new ConnectionAbortedException(),
            nameof(OperationCanceledException) => new OperationCanceledException(),
            nameof(InvalidOperationException) => new InvalidOperationException(),
            _ => throw new ArgumentOutOfRangeException(nameof(exceptionType))
        };

        await transport.CloseAsync(error, cancellationToken);

        var retried = await connection.Retries.Reader.ReadAsync(cancellationToken);
        Assert.Same(second, retried.Message);
        Assert.Same(error, retried.Error);
        // Drain the queued reroute producer before asserting there was no request replay.
        await connection.DrainAsync().WaitAsync(cancellationToken);

        Assert.False(connection.Retries.Reader.TryRead(out _));
        Assert.Equal(0, connection.SentMessageCount);
        Assert.Null(first.BodyObject);
        shared.MessageCenter.DidNotReceive().DispatchLocalMessage(Arg.Any<Message>());
        Assert.Equal(new byte[] { 2 }, Assert.IsType<byte[]>(second.BodyObject));
        var reused = shared.GetSendMessageHandler();
        Assert.Same(request, reused);
        Assert.Equal(0, reused.MessageCount);
        Assert.Equal(0, reused.Buffers.Length);
        reused.Reset();

        var log = Assert.Single(logger.ReceivedCalls(), call =>
            call.GetMethodInfo().Name == nameof(ILogger.Log)
            && ReferenceEquals(call.GetArguments()[3], error));
        Assert.Equal(expectedLogLevel, log.GetArguments()[0]);
        Assert.DoesNotContain(logger.ReceivedCalls(), call =>
            call.GetMethodInfo().Name == nameof(ILogger.Log)
            && (LogLevel)call.GetArguments()[0]! > expectedLogLevel);

        await connection.CloseAsync(null).WaitAsync(cancellationToken);
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

        request.WriteMessage(valid, default);
        var validLength = request.Length;

        Assert.Throws<InvalidMessageFrameException>(() => request.WriteMessage(invalid, default));
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

        request.WriteMessage(new Message(), default);
        Assert.False(request.HasLargeMessages);
        request.Reset();

        request = shared.GetSendMessageHandler();
        request.WriteMessage(new Message { BodyObject = new byte[8 * 1024] }, default);
        Assert.True(request.HasLargeMessages);

        request.Reset();
        var reused = shared.GetSendMessageHandler();
        Assert.Same(request, reused);
        Assert.False(reused.HasLargeMessages);

        reused.WriteMessage(new Message { BodyObject = new byte[16 * 1024] }, default);
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
        await using var transport = new CancelableTransport();
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
        await using var transport = new CancelableTransport();
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

    [Fact]
    public async Task SocketMessageTransport_ReadFin_InterruptsBlockedWrite()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var accept = listener.AcceptAsync(cancellation.Token).AsTask();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { SendBufferSize = 1024 };
        await client.ConnectAsync(listener.LocalEndPoint!, cancellation.Token);
        using var peer = await accept;
        peer.ReceiveBufferSize = 1024;
        await using var transport = new SocketMessageTransport(client, NullLogger.Instance);
        using var request = new BufferedWriteRequest(new byte[2 * 1024 * 1024]);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = transport.Closed.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), closed);
        transport.Start();

        Assert.True(transport.EnqueueWrite(request));
        Assert.Equal(1, await peer.ReceiveAsync(new byte[1], SocketFlags.None, cancellation.Token));
        Assert.False(request.Completion.IsCompleted);
        Assert.True(transport.EnqueueRead(new PendingReadRequest()));
        peer.Shutdown(SocketShutdown.Send);

        await closed.Task.WaitAsync(cancellation.Token);
        Assert.NotNull(await Record.ExceptionAsync(() => request.Completion));
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
        transport.Start(TestContext.Current.CancellationToken);
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

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    public async Task TlsEstablishmentCancellation_InterruptsAuthenticationAndFailsQueuedRequests(bool server, bool completeWrites, bool closeInnerTransport)
    {
        await using var inner = new CancelableTransport(completeWrites);
        var tlsOptions = new TlsOptions
        {
            HandshakeTimeout = Timeout.InfiniteTimeSpan,
            ClientCertificateMode = RemoteCertificateMode.NoCertificate,
            RemoteCertificateMode = RemoteCertificateMode.NoCertificate,
            LocalServerCertificateSelector = static (_, _) => null,
            OnAuthenticateAsClient = static (_, options) => options.TargetHost = "localhost"
        };
        var options = Substitute.For<IOptionsMonitor<TlsOptions>>();
        options.CurrentValue.Returns(tlsOptions);
        options.Get(Arg.Any<string>()).Returns(tlsOptions);
        using var cancellation = new CancellationTokenSource();
        await using var connector = new TlsMessageTransportConnector(new TestConnector(inner), options, NullLoggerFactory.Instance);
        await using var listener = new TlsMessageTransportListener(new TestListener(inner), options, NullLoggerFactory.Instance);
        await using var transport = server
            ? Assert.IsAssignableFrom<MessageTransport>(await listener.AcceptAsync(cancellation.Token))
            : await connector.CreateAsync(new IPEndPoint(IPAddress.Loopback, 1), cancellation.Token);
        using var request = new BufferedWriteRequest([1]);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = transport.Closed.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), closed);

        Assert.True(transport.EnqueueWrite(request));
        var pendingIo = server || completeWrites ? inner.ReadStarted.Task : inner.WriteStarted.Task;
        await pendingIo.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(request.Completion.IsCompleted);

        if (closeInnerTransport)
        {
            await inner.CloseAsync(new ConnectionClosedException(), TestContext.Current.CancellationToken);
        }
        else
        {
            cancellation.Cancel();
        }

        if (closeInnerTransport && !server && !completeWrites)
        {
            await Assert.ThrowsAsync<ConnectionClosedException>(
                () => request.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }
        else
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => request.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.True(error.CancellationToken.IsCancellationRequested);
        }

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(inner.CloseCalled);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TlsConnector_ClientAuthenticationCallbackSeesDefaultsAndCanOverrideOptions(bool checkRevocation, bool overrideRevocation)
    {
        var inner = new TrackingTransport();
        var callbackOptions = new TaskCompletionSource<(TlsClientAuthenticationOptions Options, X509RevocationMode DefaultMode)>(TaskCreationOptions.RunContinuationsAsynchronously);
        SslStream? callbackStream = null;
        var options = Substitute.For<IOptionsMonitor<TlsOptions>>();
        options.CurrentValue.Returns(new TlsOptions
        {
            ClientCertificateMode = RemoteCertificateMode.NoCertificate,
            CheckCertificateRevocation = checkRevocation,
            OnAuthenticateAsClient = (connection, sslOptions) =>
            {
                callbackStream = connection.Features.Get<SslStream>();
                sslOptions.TargetHost = "localhost";
                var defaultMode = sslOptions.CertificateRevocationCheckMode;
                if (overrideRevocation)
                {
                    sslOptions.CertificateRevocationCheckMode = checkRevocation ? X509RevocationMode.NoCheck : X509RevocationMode.Online;
                }

                callbackOptions.TrySetResult((sslOptions, defaultMode));
            }
        });
        await using var connector = new TlsMessageTransportConnector(new TestConnector(inner), options, NullLoggerFactory.Instance);
        await using var transport = await connector.CreateAsync(
            new IPEndPoint(IPAddress.Loopback, 1),
            TestContext.Current.CancellationToken);

        var (configuredOptions, defaultMode) = await callbackOptions.Task.WaitAsync(TestContext.Current.CancellationToken);
        var sslOptions = Assert.IsType<SslClientAuthenticationOptions>(configuredOptions.SslClientAuthenticationOptions);

        Assert.NotNull(callbackStream);
        Assert.Same(transport.Features.Get<SslStream>(), callbackStream);
        Assert.Equal("localhost", sslOptions.TargetHost);
        Assert.Equal([new SslApplicationProtocol("Orleans1")], sslOptions.ApplicationProtocols);
        Assert.Equal(checkRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck, defaultMode);
        var expectedMode = checkRevocation != overrideRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
        Assert.Equal(expectedMode, sslOptions.CertificateRevocationCheckMode);
    }

    [Theory]
    [InlineData(RemoteCertificateMode.NoCertificate)]
    [InlineData(RemoteCertificateMode.AllowCertificate)]
    [InlineData(RemoteCertificateMode.RequireCertificate)]
    public async Task TlsConnector_ClientCertificateSelectorHonorsCertificateMode(RemoteCertificateMode mode)
    {
        var callbackOptions = new TaskCompletionSource<TlsClientAuthenticationOptions>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = Substitute.For<IOptionsMonitor<TlsOptions>>();
        options.CurrentValue.Returns(new TlsOptions
        {
            ClientCertificateMode = mode,
            LocalClientCertificateSelector = (_, _, _, _, _) => null,
            OnAuthenticateAsClient = (_, sslOptions) =>
            {
                sslOptions.TargetHost = "localhost";
                callbackOptions.TrySetResult(sslOptions);
            }
        });
        await using var connector = new TlsMessageTransportConnector(new TestConnector(new TrackingTransport()), options, NullLoggerFactory.Instance);
        await using var transport = await connector.CreateAsync(
            new IPEndPoint(IPAddress.Loopback, 1),
            TestContext.Current.CancellationToken);
        var configuredOptions = await callbackOptions.Task.WaitAsync(TestContext.Current.CancellationToken);
        var selector = configuredOptions.LocalCertificateSelectionCallback;
        Assert.NotNull(selector);

        if (mode is RemoteCertificateMode.RequireCertificate)
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                selector(new object(), "localhost", new X509CertificateCollection(), null, []));
            Assert.Equal("No certificate provided for client authentication.", error.Message);
        }
        else
        {
            Assert.Null(selector(new object(), "localhost", new X509CertificateCollection(), null, []));
        }
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

    private static MessageHandlerShared CreateMessageHandlerShared(IServiceProvider serviceProvider, ILoggerFactory? loggerFactory = null)
    {
        var messagingInstruments = serviceProvider.GetRequiredService<MessagingInstruments>();
        var messagingTrace = new MessagingTrace(
            NullLoggerFactory.Instance,
            messagingInstruments,
            serviceProvider.GetRequiredService<MessagingProcessingInstruments>());
        return new(
            messagingTrace,
            new ConnectionTrace(loggerFactory ?? NullLoggerFactory.Instance),
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

        protected override void RetryMessage(Message msg, Exception? ex, ref AdmissionGate.Admission sendAdmission) => msg.Dispose();
        protected internal override void OnReceivedMessage(Message message) { }
        protected internal override void RecordMessageReceive(Message message, int totalBytes, int headerBytes) { }
        protected internal override void RecordMessageSend(Message message, int totalBytes, int headerBytes) { }
        public void Dispose() => _releaseSend.Dispose();
    }

    private sealed class BlockingReceiveConnection(
        MessageTransport transport,
        ConnectionCommon shared,
        IMessageCenter messageCenter,
        CancellationToken cancellationToken) : Connection(transport, shared), IDisposable
    {
        private readonly TaskCompletionSource _decoded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        public Task Decoded => _decoded.Task;
        public CorrelationId ReceivedId { get; private set; }
        public int DispatchCount { get; private set; }
        public void Release() => _release.Set();
        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.SiloToSilo;
        protected override TimeSpan CloseConnectionTimeout => TimeSpan.FromSeconds(1);
        protected override IMessageCenter MessageCenter => messageCenter;
        protected override bool PrepareMessageForSend(Message message) => true;
        protected override void RetryMessage(Message message, Exception? exception, ref AdmissionGate.Admission sendAdmission) => message.Dispose();
        protected internal override void OnReceivedMessage(Message message)
        {
            ReceivedId = message.Id;
            Assert.Equal("payload", message.BodyObject);
            _decoded.TrySetResult();
            _release.Wait(cancellationToken);
            DispatchCount++;
            message.Dispose();
        }
        protected internal override void RecordMessageReceive(Message message, int totalBytes, int headerBytes) { }
        protected internal override void RecordMessageSend(Message message, int totalBytes, int headerBytes) { }
        public void Dispose() => _release.Dispose();
    }

    private sealed class RecordingReceiveConnection(
        MessageTransport transport,
        ConnectionCommon shared,
        IMessageCenter messageCenter) : Connection(transport, shared)
    {
        public int DispatchCount { get; private set; }
        public int RecordCount { get; private set; }
        public CorrelationId RecordedId { get; private set; }
        public CorrelationId ReceivedId { get; private set; }
        public Message.Directions RecordedDirection { get; private set; }
        public bool RecordedSystemMessage { get; private set; }
        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.SiloToSilo;
        protected override TimeSpan CloseConnectionTimeout => TimeSpan.FromSeconds(1);
        protected override IMessageCenter MessageCenter => messageCenter;
        protected override bool PrepareMessageForSend(Message message) => true;
        protected override void RetryMessage(Message message, Exception? exception, ref AdmissionGate.Admission sendAdmission) => message.Dispose();
        protected internal override void OnReceivedMessage(Message message)
        {
            DispatchCount++;
            ReceivedId = message.Id;
            message.Dispose();
        }
        protected internal override void RecordMessageReceive(Message message, int totalBytes, int headerBytes)
        {
            RecordCount++;
            RecordedId = message.Id;
            RecordedDirection = message.Direction;
            RecordedSystemMessage = message.IsSystemMessage;
        }
        protected internal override void RecordMessageSend(Message message, int totalBytes, int headerBytes) { }
    }

    private sealed class BlockingRerouteConnection(
        MessageTransport transport,
        ConnectionCommon shared,
        IMessageCenter messageCenter,
        CancellationToken cancellationToken) : Connection(transport, shared), IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        public Task Entered => _entered.Task;
        public int RerouteCount { get; private set; }
        public void Release() => _release.Set();
        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.SiloToSilo;
        protected override TimeSpan CloseConnectionTimeout => TimeSpan.FromSeconds(1);
        protected override IMessageCenter MessageCenter => messageCenter;
        protected override bool PrepareMessageForSend(Message message) => true;
        protected override void RetryMessage(Message message, Exception? exception, ref AdmissionGate.Admission sendAdmission)
        {
            _entered.TrySetResult();
            _release.Wait(cancellationToken);
            RerouteCount++;
            message.Dispose();
        }
        protected internal override void OnReceivedMessage(Message message) { }
        protected internal override void RecordMessageReceive(Message message, int totalBytes, int headerBytes) { }
        protected internal override void RecordMessageSend(Message message, int totalBytes, int headerBytes) { }
        public void Dispose() => _release.Dispose();
    }

    private sealed class SignalingSendConnection(MessageTransport transport, ConnectionCommon shared, IMessageCenter messageCenter)
        : Connection(transport, shared), IDisposable
    {
        private readonly TaskCompletionSource _prepareEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PrepareEntered => _prepareEntered.Task;
        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.SiloToSilo;
        protected override TimeSpan CloseConnectionTimeout => TimeSpan.FromSeconds(1);
        protected override IMessageCenter MessageCenter => messageCenter;
        protected override bool PrepareMessageForSend(Message message) { _prepareEntered.TrySetResult(); return true; }
        protected override void RetryMessage(Message message, Exception? exception, ref AdmissionGate.Admission sendAdmission) => message.Dispose();
        protected internal override void OnReceivedMessage(Message message) { }
        protected internal override void RecordMessageReceive(Message message, int totalBytes, int headerBytes) { }
        protected internal override void RecordMessageSend(Message message, int totalBytes, int headerBytes) { }
        public void Dispose() { }
    }

    private sealed class HeldRetryConnection(MessageTransport transport, ConnectionCommon shared, IMessageCenter messageCenter,
        Connection replacement, CancellationToken cancellationToken) : Connection(transport, shared), IDisposable
    {
        private readonly TaskCompletionSource _retryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        public Task RetryEntered => _retryEntered.Task;
        public void Release() => _release.Set();
        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.SiloToSilo;
        protected override TimeSpan CloseConnectionTimeout => TimeSpan.FromSeconds(1);
        protected override IMessageCenter MessageCenter => messageCenter;
        protected override bool PrepareMessageForSend(Message message) => true;
        protected override void RetryMessage(Message message, Exception? exception, ref AdmissionGate.Admission sendAdmission)
        {
            _retryEntered.TrySetResult();
            _release.Wait(cancellationToken);
            replacement.Send(message, ref sendAdmission);
        }
        protected internal override void OnReceivedMessage(Message message) { }
        protected internal override void RecordMessageReceive(Message message, int totalBytes, int headerBytes) { }
        protected internal override void RecordMessageSend(Message message, int totalBytes, int headerBytes) { }
        public void Dispose() => _release.Dispose();
    }

    private sealed class RejectingWriteTransport : MessageTransport
    {
        public int Attempts { get; private set; }
        public override CancellationToken Closed => default;
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override bool EnqueueRead(ReadRequest request) => false;
        public override bool EnqueueWrite(WriteRequest request) { Attempts++; return false; }
        public override ValueTask CloseAsync(Exception? closeException, CancellationToken cancellationToken = default) => default;
    }

    private sealed class BoundaryCompletionSource : IResponseCompletionSource
    {
        public Response? Response { get; private set; }
        public int CompletionCount { get; private set; }
        public void Complete(Response response) { Response = response; CompletionCount++; }
        public void Complete() => Complete(Orleans.Serialization.Invocation.Response.Completed);
    }

    private sealed class CancelableTransport(bool completeWrites = false) : MessageTransport
    {
        private readonly CancellationTokenSource _closed = new();
        private ReadRequest? _read;
        private WriteRequest? _write;
        private int _disposed;

        public bool CloseCalled => _closed.IsCancellationRequested;
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WriteCount { get; private set; }

        public void CompleteWrite()
        {
            var write = Interlocked.Exchange(ref _write, null) ?? throw new InvalidOperationException("No accepted write is pending.");
            using var bytes = write.Buffers.ConsumeSlice(write.Buffers.Length);
            write.SetResult();
        }
        public override CancellationToken Closed => _closed.Token;
        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override bool EnqueueRead(ReadRequest request)
        {
            if (CloseCalled)
            {
                return false;
            }

            _read = request;
            ReadStarted.TrySetResult();
            return true;
        }

        public override bool EnqueueWrite(WriteRequest request)
        {
            if (CloseCalled)
            {
                return false;
            }

            if (completeWrites)
            {
                using var bytes = request.Buffers.ConsumeSlice(request.Buffers.Length);
                request.SetResult();
            }
            else
            {
                _write = request;
            }

            WriteCount++;
            WriteStarted.TrySetResult();
            return true;
        }

        public override ValueTask CloseAsync(Exception? closeException, CancellationToken cancellationToken = default)
        {
            if (CloseCalled)
            {
                return default;
            }

            _closed.Cancel();
            var error = closeException ?? new ConnectionClosedException();
            var read = Interlocked.Exchange(ref _read, null);
            read?.OnCanceled();
            var write = Interlocked.Exchange(ref _write, null);
            write?.SetException(error);
            return default;
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await CloseAsync(null);
                _closed.Dispose();
            }
        }
    }

    private sealed class RetryTrackingConnection(MessageTransport transport, ConnectionCommon shared, IMessageCenter messageCenter)
        : Connection(transport, shared)
    {
        public Channel<(Message Message, Exception? Error)> Retries { get; } =
            Channel.CreateUnbounded<(Message, Exception?)>();
        public int SentMessageCount { get; private set; }
        public int ApplicationWriteFailures { get; private set; }
        internal override void OnApplicationWriteFailure(Message message) => ApplicationWriteFailures++;
        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.SiloToSilo;
        protected override TimeSpan CloseConnectionTimeout => TimeSpan.FromSeconds(1);
        protected override IMessageCenter MessageCenter => messageCenter;
        protected override bool PrepareMessageForSend(Message msg) => true;
        protected override void RetryMessage(Message msg, Exception? ex, ref AdmissionGate.Admission sendAdmission) => Retries.Writer.TryWrite((msg, ex));
        protected internal override void OnReceivedMessage(Message message) { }
        protected internal override void RecordMessageReceive(Message message, int totalBytes, int headerBytes) { }
        protected internal override void RecordMessageSend(Message message, int totalBytes, int headerBytes) => SentMessageCount++;
    }

    private sealed class CapturingTransport : MessageTransport
    {
        public byte[]? Written { get; private set; }
        public Channel<byte[]> Writes { get; } = Channel.CreateUnbounded<byte[]>();
        public override CancellationToken Closed => default;
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override bool EnqueueRead(ReadRequest request) => false;

        public override bool EnqueueWrite(WriteRequest request)
        {
            Written = new byte[request.Buffers.Length];
            request.Buffers.Consume(Written);
            request.SetResult();
            Writes.Writer.TryWrite(Written);
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

    private sealed class PendingReadRequest : ReadRequest
    {
        public override bool OnRead(ArcBufferReader buffer) => false;
        public override void OnError(Exception error) { }
        public override void OnCanceled() { }
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

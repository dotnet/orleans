#nullable enable
using System;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Connections;
using Orleans.Connections.Transport;
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
    public void MessageSerializer_Write_CopiesBufferedRawResponse()
    {
        using var serviceProvider = CreateServiceProvider();
        var sessionPool = serviceProvider.GetRequiredService<SerializerSessionPool>();
        var serializer = new MessageSerializer(sessionPool, new SiloMessagingOptions());
        var shared = CreateMessageHandlerShared(serviceProvider);
        using var bodyWriter = new ArcBufferWriter();
        byte[] bodyBytes = [1, 2, 3, 4];
        bodyWriter.Write(bodyBytes);

        var readRequest = new MessageReadRequest(shared);
        readRequest._originalHeaders.ResponseType = Message.ResponseTypes.Success;
        readRequest.Body = bodyWriter.ConsumeSlice(bodyBytes.Length);
        typeof(MessageReadRequest).GetField("_bodyLength", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(readRequest, bodyBytes.Length);

        var message = new Message
        {
            Direction = Message.Directions.Response,
            Result = Message.ResponseTypes.Success,
            BodyObject = readRequest
        };

        using var output = new ArcBufferWriter();
        var (headerLength, bodyLength) = serializer.Write(output, message);

        Assert.Equal(bodyBytes.Length, bodyLength);
        var outputBytes = new byte[output.Length];
        output.Peek(outputBytes);
        Assert.Equal(bodyBytes, outputBytes[headerLength..(headerLength + bodyLength)]);
        Assert.Null(message._bodyObject);
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

    private static ServiceProvider CreateServiceProvider() => new ServiceCollection()
        .AddSerializer()
        .AddTransient(sp => new MessageSerializer(sp.GetRequiredService<SerializerSessionPool>(), new SiloMessagingOptions()))
        .BuildServiceProvider();

    private static MessageHandlerShared CreateMessageHandlerShared(IServiceProvider serviceProvider)
    {
        var messagingTrace = new MessagingTrace(NullLoggerFactory.Instance);
        return new(
            messagingTrace,
            new ConnectionTrace(NullLoggerFactory.Instance),
            serviceProvider,
            new MessageFactory(serviceProvider.GetRequiredService<DeepCopier>(), NullLogger<MessageFactory>.Instance, messagingTrace),
            Substitute.For<IMessageCenter>());
    }
}

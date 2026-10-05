using System;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Connections;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;

namespace UnitTests.Runtime;

internal sealed class BufferedResponse : IDisposable
{
    private readonly MessageHandlerShared _shared;

    public BufferedResponse(IServiceProvider services, CorrelationId id, GrainId targetGrain, bool malformed)
        : this(services, new Message
        {
            Direction = Message.Directions.Response,
            Result = Message.ResponseTypes.Status,
            Id = id,
            TargetGrain = targetGrain,
            BodyObject = new StatusResponse(isExecuting: true, isWaiting: false, ["processing"])
        }, malformed)
    {
    }

    public BufferedResponse(IServiceProvider services, Message message, bool malformed = false)
    {
        var instruments = new OrleansInstruments(services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
        var messaging = new MessagingInstruments(instruments);
        var trace = new MessagingTrace(NullLoggerFactory.Instance, messaging, new MessagingProcessingInstruments(instruments));
        var factory = new MessageFactory(services.GetRequiredService<DeepCopier>(), NullLogger<MessageFactory>.Instance, trace);
        var sessions = services.GetRequiredService<SerializerSessionPool>();
        _shared = new MessageHandlerShared(
            trace,
            new ConnectionTrace(NullLoggerFactory.Instance),
            () => new MessageSerializer(sessions, new SiloMessagingOptions()),
            factory,
            Substitute.For<IMessageCenter>(),
            messaging);
        Message = message;
        using var buffer = new ArcBufferWriter();
        int bodyLength;
        if (malformed)
        {
            buffer.Write([0xff]);
            bodyLength = 1;
        }
        else
        {
            using var serializer = new MessageSerializer(sessions, new SiloMessagingOptions());
            var lengths = serializer.Write(buffer, Message);
            buffer.AdvanceReader(lengths.HeaderLength);
            bodyLength = lengths.BodyLength;
        }

        Request = _shared.GetReceiveMessageHandler();
        Request._originalResponseType = Message.Result;
        Request.Body = buffer.ConsumeSlice(bodyLength);
        typeof(MessageReadRequest).GetField("_bodyLength", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Request, bodyLength);
        Message.SetMessageReadRequest(Request);
    }

    public Message Message { get; }
    public MessageReadRequest Request { get; }

    public void Dispose()
    {
        Message.Dispose();
        _shared.Dispose();
    }
}

#nullable enable
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Connections;

namespace Orleans.Runtime.Messaging;

internal sealed class MessageHandlerShared(
    MessagingTrace messagingTrace,
    ConnectionTrace connectionTrace,
    IServiceProvider serviceProvider,
    MessageFactory messageFactory,
    IMessageCenter messageCenter,
    MessagingInstruments messagingInstruments)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ConcurrentQueue<MessageSerializer> _serializerPool = new();
    private readonly ConcurrentQueue<MessageReadRequest> _receivePool = new();
    private readonly ConcurrentQueue<MessageWriteRequest> _sendPool = new();

    public MessagingTrace MessagingTrace { get; } = messagingTrace;
    public ConnectionTrace ConnectionTrace { get; } = connectionTrace;
    public MessageFactory MessageFactory { get; } = messageFactory;
    public IMessageCenter MessageCenter { get; } = messageCenter;
    public MessagingInstruments MessagingInstruments { get; } = messagingInstruments;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MessageSerializer GetMessageSerializer()
    {
        if (_serializerPool.TryDequeue(out var result))
        {
            return result;
        }

        return _serviceProvider.GetRequiredService<MessageSerializer>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Return(MessageSerializer serializer) => _serializerPool.Enqueue(serializer);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MessageReadRequest GetReceiveMessageHandler()
    {
        if (_receivePool.TryDequeue(out var result))
        {
            return result;
        }

        return new(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Return(MessageReadRequest handler) => _receivePool.Enqueue(handler);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MessageWriteRequest GetSendMessageHandler()
    {
        if (_sendPool.TryDequeue(out var result))
        {
            return result;
        }

        return new(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MessageWriteRequest GetSendMessageHandler(Connection connection)
    {
        var result = GetSendMessageHandler();
        result.Initialize(connection);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Return(MessageWriteRequest handler) => _sendPool.Enqueue(handler);
}

#nullable enable
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Connections;

namespace Orleans.Runtime.Messaging
{
    internal sealed class MessageHandlerShared(
        MessagingTrace messagingTrace,
        ConnectionTrace connectionTrace,
        IServiceProvider serviceProvider,
        MessageFactory messageFactory,
        IMessageCenter messageCenter)
    {
        private readonly IServiceProvider _serviceProvider = serviceProvider;
        private readonly ConcurrentStack<MessageSerializer> _serializerPool = new();
        private readonly ConcurrentStack<MessageReadRequest> _receivePool = new();
        private readonly ConcurrentStack<MessageWriteRequest> _sendPool = new();

        public MessagingTrace MessagingTrace { get; } = messagingTrace;
        public ConnectionTrace ConnectionTrace { get; } = connectionTrace;
        public MessageFactory MessageFactory { get; } = messageFactory;
        public IMessageCenter MessageCenter { get; } = messageCenter;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal MessageSerializer GetMessageSerializer()
        {
            if (_serializerPool.TryPop(out var result))
            {
                return result;
            }

            return _serviceProvider.GetRequiredService<MessageSerializer>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Return(MessageSerializer serializer)
        {
            _serializerPool.Push(serializer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal MessageReadRequest GetReceiveMessageHandler()
        {
            if (_receivePool.TryPop(out var result))
            {
                return result;
            }

            return new(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Return(MessageReadRequest handler)
        {
            _receivePool.Push(handler);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal MessageWriteRequest GetSendMessageHandler()
        {
            if (_sendPool.TryPop(out var result))
            {
                return result;
            }

            return new(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Return(MessageWriteRequest handler)
        {
            _sendPool.Push(handler);
        }
    }
}

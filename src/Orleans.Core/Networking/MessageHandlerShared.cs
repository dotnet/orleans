#nullable enable
using System;
using System.Collections.Generic;
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

        public MessagingTrace MessagingTrace { get; } = messagingTrace;
        public ConnectionTrace ConnectionTrace { get; } = connectionTrace;
        public MessageFactory MessageFactory { get; } = messageFactory;
        public IMessageCenter MessageCenter { get; } = messageCenter;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal MessageSerializer GetMessageSerializer()
        {
            var stack = SerializerPool.Stack ??= new();
            if (stack.TryPop(out var result))
            {
                return result;
            }

            return _serviceProvider.GetRequiredService<MessageSerializer>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Return(MessageSerializer serializer)
        {
            var stack = SerializerPool.Stack ??= new();
            stack.Push(serializer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal MessageReadRequest GetReceiveMessageHandler()
        {
            var stack = ReceivePool.Stack ??= new();
            if (stack.TryPop(out var result))
            {
                return result;
            }

            return new(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Return(MessageReadRequest handler)
        {
            var stack = ReceivePool.Stack ??= new();
            stack.Push(handler);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal MessageWriteRequest GetSendMessageHandler()
        {
            var stack = SendPool.Stack ??= new();
            if (stack.TryPop(out var result))
            {
                return result;
            }

            return new(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Return(MessageWriteRequest handler)
        {
            var stack = SendPool.Stack ??= new();
            stack.Push(handler);
        }

        // Thread-local pools using nested classes to ensure proper ThreadStatic semantics per type
        private static class SerializerPool
        {
            [ThreadStatic]
            internal static Stack<MessageSerializer>? Stack;
        }

        private static class ReceivePool
        {
            [ThreadStatic]
            internal static Stack<MessageReadRequest>? Stack;
        }

        private static class SendPool
        {
            [ThreadStatic]
            internal static Stack<MessageWriteRequest>? Stack;
        }
    }
}

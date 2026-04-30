#nullable enable

using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Connections;
using Orleans.Placement.Repartitioning;

namespace Orleans.Runtime.Messaging;

internal sealed class ConnectionCommon(
    IServiceProvider serviceProvider,
    MessageFactory messageFactory,
    MessagingTrace messagingTrace,
    ConnectionTrace networkingTrace,
    IMessageStatisticsSink messageStatisticsSink)
{
    private readonly object _lock = new();
    private MessageHandlerShared? _messageHandlerShared;

    public MessageFactory MessageFactory { get; } = messageFactory;
    public IServiceProvider ServiceProvider { get; } = serviceProvider;
    public ConnectionTrace ConnectionTrace { get; } = networkingTrace;
    public MessagingTrace MessagingTrace { get; } = messagingTrace;
    public Action<Message>? MessageObserver { get; } = messageStatisticsSink.GetMessageObserver();

    public MessageHandlerShared MessageHandlerShared
    {
        get
        {
            if (_messageHandlerShared is { } value) return value;
            lock (_lock)
            {
                return _messageHandlerShared ??= ServiceProvider.GetRequiredService<MessageHandlerShared>();
            }
        }
    }
}

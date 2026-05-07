using System;
using Microsoft.Extensions.Logging;
using Orleans.Placement.Repartitioning;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ConnectionCommon(
        IServiceProvider serviceProvider,
        MessageFactory messageFactory,
        MessagingTrace messagingTrace,
        ILogger<Connection> logger,
        IMessageStatisticsSink messageStatisticsSink)
    {
        public MessageFactory MessageFactory { get; } = messageFactory;
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public ILogger<Connection> Logger { get; } = logger;
        public IMessageStatisticsSink MessageStatisticsSink { get; } = messageStatisticsSink;
        public MessagingTrace MessagingTrace { get; } = messagingTrace;
    }
}

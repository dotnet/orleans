using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Networking.Shared;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ClientOutboundConnectionFactory : ConnectionFactory
    {
        private readonly IServiceProvider serviceProvider;
        private readonly MessageFactory messageFactory;
        private readonly INetworkingTrace trace;
        private GatewayManager gatewayManager;
        private ClientMessageCenter messageCenter;

        public ClientOutboundConnectionFactory(
            IServiceProvider serviceProvider,
            IOptions<ConnectionOptions> connectionOptions,
            IConnectionFactory connectionFactory,
            MessageFactory messageFactory,
            INetworkingTrace trace)
            : base(connectionFactory, serviceProvider, connectionOptions)
        {
            this.serviceProvider = serviceProvider;
            this.messageFactory = messageFactory;
            this.trace = trace;
        }

        protected override Connection CreateConnection(ConnectionContext context)
        {
            if (this.messageCenter is null) this.messageCenter = this.serviceProvider.GetRequiredService<ClientMessageCenter>();
            if (this.gatewayManager is null) this.gatewayManager = this.serviceProvider.GetRequiredService<GatewayManager>();
            return new ClientOutboundConnection(
                context,
                this.ConnectionDelegate,
                this.messageFactory,
                this.serviceProvider,
                this.messageCenter,
                this.gatewayManager,
                this.trace);
        }
    }
}

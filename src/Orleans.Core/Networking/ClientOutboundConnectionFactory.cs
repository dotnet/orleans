using System;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ClientOutboundConnectionFactory : ConnectionFactory
    {
        private readonly IServiceProvider serviceProvider;
        private readonly MessageFactory messageFactory;
        private readonly INetworkingTrace trace;
        private readonly object initializationLock = new object();
        private bool isInitialized;
        private GatewayManager gatewayManager;
        private ClientMessageCenter messageCenter;
        private ConnectionManager connectionManager;

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

        protected override Connection CreateConnection(SiloAddress address, ConnectionContext context)
        {
            EnsureInitialized();

            return new ClientOutboundConnection(
                address,
                context,
                this.ConnectionDelegate,
                this.messageFactory,
                this.serviceProvider,
                this.messageCenter,
                this.trace,
                this.connectionManager,
                this.ConnectionOptions);
        }

        private void EnsureInitialized()
        {
            if (!isInitialized)
            {
                lock (this.initializationLock)
                {
                    if (!isInitialized)
                    {
                        this.messageCenter = this.serviceProvider.GetRequiredService<ClientMessageCenter>();
                        this.gatewayManager = this.serviceProvider.GetRequiredService<GatewayManager>();
                        this.connectionManager = this.serviceProvider.GetRequiredService<ConnectionManager>();
                        this.isInitialized = true;
                    }
                }
            }
        }
    }
}

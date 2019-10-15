using System;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ClientOutboundConnectionFactory : ConnectionFactory, IConnectionDirectionFeature
    {
        internal static readonly object ServicesKey = new object();
        private readonly IServiceProvider serviceProvider;
        private readonly ClientConnectionOptions clientConnectionOptions;
        private readonly MessageFactory messageFactory;
        private readonly INetworkingTrace trace;
        private readonly object initializationLock = new object();
        private bool isInitialized;
        private ClientMessageCenter messageCenter;
        private ConnectionManager connectionManager;

        public ClientOutboundConnectionFactory(
            IServiceProvider serviceProvider,
            IOptions<ConnectionOptions> connectionOptions,
            IOptions<ClientConnectionOptions> clientConnectionOptions,
            MessageFactory messageFactory,
            INetworkingTrace trace)
            : base(serviceProvider.GetRequiredServiceByKey<object, IConnectionFactory>(ServicesKey), serviceProvider, connectionOptions)
        {
            this.serviceProvider = serviceProvider;
            this.clientConnectionOptions = clientConnectionOptions.Value;
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

        protected override void ConfigureConnectionBuilder(IConnectionBuilder connectionBuilder)
        {
            connectionBuilder.Use(next =>
            {
                return async context =>
                {
                    context.Features.Set<IConnectionDirectionFeature>(this);
                    await next(context);
                };
            });
            this.clientConnectionOptions.ConfigureConnectionBuilder(connectionBuilder);
            base.ConfigureConnectionBuilder(connectionBuilder);
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
                        this.connectionManager = this.serviceProvider.GetRequiredService<ConnectionManager>();
                        this.isInitialized = true;
                    }
                }
            }
        }

        bool IConnectionDirectionFeature.IsOutboundConnection => true;
    }
}

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Networking.Shared;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SiloConnectionFactory : ConnectionFactory
    {
        private readonly INetworkingTrace trace;
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly IServiceProvider serviceProvider;
        private readonly MessageFactory messageFactory;
        private readonly object initializationLock = new object();
        private bool isInitialized;
        private ConnectionManager connectionManager;
        private MessageCenter messageCenter;

        public SiloConnectionFactory(
            IServiceProvider serviceProvider,
            IOptions<ConnectionOptions> connectionOptions,
            IConnectionFactory connectionFactory,
            MessageFactory messageFactory,
            INetworkingTrace trace,
            ILocalSiloDetails localSiloDetails,
            ISiloStatusOracle siloStatusOracle)
            : base(connectionFactory, serviceProvider, connectionOptions)
        {
            this.serviceProvider = serviceProvider;
            this.messageFactory = messageFactory;
            this.trace = trace;
            this.localSiloDetails = localSiloDetails;
            this.siloStatusOracle = siloStatusOracle;
        }

        protected override Connection CreateConnection(SiloAddress address, ConnectionContext context)
        {
            EnsureInitialized();

            return new SiloConnection(
                address,
                context,
                this.ConnectionDelegate,
                this.serviceProvider,
                this.trace,
                this.messageCenter,
                this.messageFactory,
                this.localSiloDetails,
                this.siloStatusOracle,
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
                        this.messageCenter = this.serviceProvider.GetRequiredService<MessageCenter>();
                        this.connectionManager = this.serviceProvider.GetRequiredService<ConnectionManager>();
                        this.isInitialized = true;
                    }
                }
            }
        }
    }
}

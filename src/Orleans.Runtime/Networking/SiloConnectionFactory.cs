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

        protected override Connection CreateConnection(ConnectionContext context)
        {
            if (this.messageCenter is null) this.messageCenter = this.serviceProvider.GetRequiredService<MessageCenter>();

            return new SiloConnection(
                context,
                this.ConnectionDelegate,
                this.serviceProvider,
                this.trace,
                this.messageCenter,
                this.messageFactory,
                this.localSiloDetails,
                this.siloStatusOracle);
        }
    }
}

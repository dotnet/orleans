using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ClientOutboundConnectionFactory : ConnectionFactory
    {
        internal static readonly object ServicesKey = new object();
        private readonly ConnectionCommon connectionShared;
        private readonly ClientConnectionOptions clientConnectionOptions;
        private readonly ClusterOptions clusterOptions;
        private readonly ConnectionPreambleHelper connectionPreambleHelper;
        private readonly object initializationLock = new object();
        private volatile bool isInitialized;
        private ClientMessageCenter messageCenter;
        private ConnectionManager connectionManager;

        public ClientOutboundConnectionFactory(
            IOptions<ConnectionOptions> connectionOptions,
            IOptions<ClientConnectionOptions> clientConnectionOptions,
            IOptions<ClusterOptions> clusterOptions,
            ConnectionCommon connectionShared,
            ConnectionPreambleHelper connectionPreambleHelper)
            : base(connectionShared.ServiceProvider.GetRequiredServiceByKey<object, IConnectionFactory>(ServicesKey), connectionShared.ServiceProvider, connectionOptions)
        {
            this.connectionShared = connectionShared;
            this.clientConnectionOptions = clientConnectionOptions.Value;
            this.clusterOptions = clusterOptions.Value;
            this.connectionPreambleHelper = connectionPreambleHelper;
        }

        protected override Connection CreateConnection(SiloAddress address, ConnectionContext context)
        {
            EnsureInitialized();

            return new ClientOutboundConnection(
                address,
                context,
                this.ConnectionDelegate,
                this.messageCenter,
                this.connectionManager,
                this.ConnectionOptions,
                this.connectionShared,
                this.connectionPreambleHelper,
                this.clusterOptions);
        }

        protected override void ConfigureConnectionBuilder(IConnectionBuilder connectionBuilder)
        {
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
                        this.messageCenter = this.connectionShared.ServiceProvider.GetRequiredService<ClientMessageCenter>();
                        this.connectionManager = this.connectionShared.ServiceProvider.GetRequiredService<ConnectionManager>();
                        this.isInitialized = true;
                    }
                }
            }
        }
    }
}

using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ClientOutboundConnectionFactory(
        IOptions<ConnectionOptions> connectionOptions,
        IOptions<ClientConnectionOptions> clientConnectionOptions,
        IOptions<ClusterOptions> clusterOptions,
        ConnectionCommon connectionShared,
        ConnectionPreambleHelper connectionPreambleHelper)
        : ConnectionFactory(connectionShared.ServiceProvider.GetRequiredKeyedService<IConnectionFactory>(ServicesKey),
            connectionShared.ServiceProvider, connectionOptions)
    {
        internal static readonly object ServicesKey = new object();
        private readonly ClientConnectionOptions clientConnectionOptions = clientConnectionOptions.Value;
        private readonly ClusterOptions clusterOptions = clusterOptions.Value;
        private readonly object initializationLock = new object();
        private volatile bool isInitialized;
        private ClientMessageCenter messageCenter;
        private ConnectionManager connectionManager;

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
                connectionShared,
                connectionPreambleHelper,
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
                        this.messageCenter = connectionShared.ServiceProvider.GetRequiredService<ClientMessageCenter>();
                        this.connectionManager = connectionShared.ServiceProvider.GetRequiredService<ConnectionManager>();
                        this.isInitialized = true;
                    }
                }
            }
        }
    }
}

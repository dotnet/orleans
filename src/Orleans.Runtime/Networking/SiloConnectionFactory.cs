using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SiloConnectionFactory : ConnectionFactory
    {
        internal static readonly object ServicesKey = new object();
        private readonly INetworkingTrace trace;
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly IServiceProvider serviceProvider;
        private readonly SiloConnectionOptions siloConnectionOptions;
        private readonly MessageFactory messageFactory;
        private readonly object initializationLock = new object();
        private bool isInitialized;
        private ConnectionManager connectionManager;
        private MessageCenter messageCenter;
        private ISiloStatusOracle siloStatusOracle;

        public SiloConnectionFactory(
            IServiceProvider serviceProvider,
            IOptions<ConnectionOptions> connectionOptions,
            IOptions<SiloConnectionOptions> siloConnectionOptions,
            MessageFactory messageFactory,
            INetworkingTrace trace,
            ILocalSiloDetails localSiloDetails)
            : base(serviceProvider.GetRequiredServiceByKey<object, IConnectionFactory>(ServicesKey), serviceProvider, connectionOptions)
        {
            this.serviceProvider = serviceProvider;
            this.siloConnectionOptions = siloConnectionOptions.Value;
            this.messageFactory = messageFactory;
            this.trace = trace;
            this.localSiloDetails = localSiloDetails;
        }

        public override ValueTask<Connection> ConnectAsync(SiloAddress address, CancellationToken cancellationToken)
        {
            EnsureInitialized();

            if (this.siloStatusOracle.IsDeadSilo(address))
            {
                throw new ConnectionAbortedException($"Denying connection to known-dead silo {address}");
            }

            return base.ConnectAsync(address, cancellationToken);
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
                this.connectionManager,
                this.ConnectionOptions);
        }

        protected override void ConfigureConnectionBuilder(IConnectionBuilder connectionBuilder)
        {
            var configureDelegate = (SiloConnectionOptions.ISiloConnectionBuilderOptions)this.siloConnectionOptions;
            configureDelegate.ConfigureSiloOutboundBuilder(connectionBuilder);
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
                        this.messageCenter = this.serviceProvider.GetRequiredService<MessageCenter>();
                        this.connectionManager = this.serviceProvider.GetRequiredService<ConnectionManager>();
                        this.siloStatusOracle = this.serviceProvider.GetRequiredService<ISiloStatusOracle>();
                        this.isInitialized = true;
                    }
                }
            }
        }
    }
}

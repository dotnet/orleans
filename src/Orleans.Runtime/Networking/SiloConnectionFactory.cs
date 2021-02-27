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
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly ConnectionCommon connectionShared;
        private readonly ProbeRequestMonitor probeRequestMonitor;
        private readonly ConnectionPreambleHelper connectionPreambleHelper;
        private readonly IServiceProvider serviceProvider;
        private readonly SiloConnectionOptions siloConnectionOptions;
        private readonly object initializationLock = new object();
        private bool isInitialized;
        private ConnectionManager connectionManager;
        private MessageCenter messageCenter;
        private ISiloStatusOracle siloStatusOracle;

        public SiloConnectionFactory(
            IServiceProvider serviceProvider,
            IOptions<ConnectionOptions> connectionOptions,
            IOptions<SiloConnectionOptions> siloConnectionOptions,
            ILocalSiloDetails localSiloDetails,
            ConnectionCommon connectionShared,
            ProbeRequestMonitor probeRequestMonitor,
            ConnectionPreambleHelper connectionPreambleHelper)
            : base(serviceProvider.GetRequiredServiceByKey<object, IConnectionFactory>(ServicesKey), serviceProvider, connectionOptions)
        {
            this.serviceProvider = serviceProvider;
            this.siloConnectionOptions = siloConnectionOptions.Value;
            this.localSiloDetails = localSiloDetails;
            this.connectionShared = connectionShared;
            this.probeRequestMonitor = probeRequestMonitor;
            this.connectionPreambleHelper = connectionPreambleHelper;
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
                this.messageCenter,
                this.localSiloDetails,
                this.connectionManager,
                this.ConnectionOptions,
                this.connectionShared,
                this.probeRequestMonitor,
                this.connectionPreambleHelper);
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

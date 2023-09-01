using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SiloConnectionListener : ConnectionListener, ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver
    {
        internal static readonly object ServicesKey = new object();
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly SiloConnectionOptions siloConnectionOptions;
        private readonly MessageCenter messageCenter;
        private readonly EndpointOptions endpointOptions;
        private readonly ConnectionManager connectionManager;
        private readonly ConnectionCommon connectionShared;
        private readonly ProbeRequestMonitor probeRequestMonitor;
        private readonly ConnectionPreambleHelper connectionPreambleHelper;

        public SiloConnectionListener(
            IServiceProvider serviceProvider,
            IOptions<ConnectionOptions> connectionOptions,
            IOptions<SiloConnectionOptions> siloConnectionOptions,
            MessageCenter messageCenter,
            IOptions<EndpointOptions> endpointOptions,
            ILocalSiloDetails localSiloDetails,
            ConnectionManager connectionManager,
            ConnectionCommon connectionShared,
            ProbeRequestMonitor probeRequestMonitor,
            ConnectionPreambleHelper connectionPreambleHelper)
            : base(serviceProvider.GetRequiredServiceByKey<object, IConnectionListenerFactory>(ServicesKey), connectionOptions, connectionManager, connectionShared)
        {
            this.siloConnectionOptions = siloConnectionOptions.Value;
            this.messageCenter = messageCenter;
            this.localSiloDetails = localSiloDetails;
            this.connectionManager = connectionManager;
            this.connectionShared = connectionShared;
            this.probeRequestMonitor = probeRequestMonitor;
            this.connectionPreambleHelper = connectionPreambleHelper;
            this.endpointOptions = endpointOptions.Value;
        }

        public override EndPoint Endpoint => this.endpointOptions.GetListeningSiloEndpoint();

        protected override Connection CreateConnection(ConnectionContext context)
        {
            return new SiloConnection(
                default,
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
            configureDelegate.ConfigureSiloInboundBuilder(connectionBuilder);
            base.ConfigureConnectionBuilder(connectionBuilder);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            if (this.Endpoint is null) return;

            lifecycle.Subscribe(nameof(SiloConnectionListener), ServiceLifecycleStage.RuntimeInitialize - 1, this);
        }

        Task ILifecycleObserver.OnStart(CancellationToken ct) => Task.Run(async () =>
        {
            await BindAsync();
            // Start accepting connections immediately.
            Start();
        });

        Task ILifecycleObserver.OnStop(CancellationToken ct) => Task.Run(() => StopAsync(ct));
    }
}

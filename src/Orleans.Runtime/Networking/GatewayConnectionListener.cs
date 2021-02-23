using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Orleans.Runtime.Messaging
{
    internal sealed class GatewayConnectionListener : ConnectionListener, ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver
    {
        internal static readonly object ServicesKey = new object();
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly MessageCenter messageCenter;
        private readonly ConnectionCommon connectionShared;
        private readonly ILogger<GatewayConnectionListener> logger;
        private readonly EndpointOptions endpointOptions;
        private readonly SiloConnectionOptions siloConnectionOptions;
        private readonly OverloadDetector overloadDetector;
        private readonly Gateway gateway;

        public GatewayConnectionListener(
            IServiceProvider serviceProvider,
            IOptions<ConnectionOptions> connectionOptions,
            IOptions<SiloConnectionOptions> siloConnectionOptions,
            OverloadDetector overloadDetector,
            ILocalSiloDetails localSiloDetails,
            IOptions<EndpointOptions> endpointOptions,
            MessageCenter messageCenter,
            ConnectionManager connectionManager,
            ConnectionCommon connectionShared,
            ILogger<GatewayConnectionListener> logger)
            : base(serviceProvider.GetRequiredServiceByKey<object, IConnectionListenerFactory>(ServicesKey), connectionOptions, connectionManager, connectionShared)
        {
            this.siloConnectionOptions = siloConnectionOptions.Value;
            this.overloadDetector = overloadDetector;
            this.gateway = messageCenter.Gateway;
            this.localSiloDetails = localSiloDetails;
            this.messageCenter = messageCenter;
            this.connectionShared = connectionShared;
            this.logger = logger;
            this.endpointOptions = endpointOptions.Value;
        }

        public override EndPoint Endpoint => this.endpointOptions.GetListeningProxyEndpoint();

        protected override Connection CreateConnection(ConnectionContext context)
        {
            return new GatewayInboundConnection(
                context,
                this.ConnectionDelegate,
                this.gateway,
                this.overloadDetector,
                this.localSiloDetails,
                this.ConnectionOptions,
                this.messageCenter,
                this.connectionShared);
        }

        protected override void ConfigureConnectionBuilder(IConnectionBuilder connectionBuilder)
        {
            var configureDelegate = (SiloConnectionOptions.ISiloConnectionBuilderOptions)this.siloConnectionOptions;
            configureDelegate.ConfigureGatewayInboundBuilder(connectionBuilder);
            base.ConfigureConnectionBuilder(connectionBuilder);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            if (this.Endpoint is null) return;

            lifecycle.Subscribe(nameof(GatewayConnectionListener), ServiceLifecycleStage.RuntimeInitialize - 1, this);
            lifecycle.Subscribe(nameof(GatewayConnectionListener), ServiceLifecycleStage.Active, _ => Task.Run(Start));
        }

        Task ILifecycleObserver.OnStart(CancellationToken ct) => Task.Run(BindAsync);
        Task ILifecycleObserver.OnStop(CancellationToken ct) => Task.Run(() => StopAsync(ct));
    }
}

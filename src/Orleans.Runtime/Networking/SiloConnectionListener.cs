using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SiloConnectionListener : ConnectionListener, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly INetworkingTrace trace;
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly SiloConnectionOptions siloConnectionOptions;
        private readonly MessageCenter messageCenter;
        private readonly MessageFactory messageFactory;
        private readonly EndpointOptions endpointOptions;
        private readonly ConnectionManager connectionManager;

        public SiloConnectionListener(
            IServiceProvider serviceProvider,
            IOptions<ConnectionOptions> connectionOptions,
            IOptions<SiloConnectionOptions> siloConnectionOptions,
            IConnectionListenerFactory listenerFactory,
            MessageCenter messageCenter,
            MessageFactory messageFactory,
            INetworkingTrace trace,
            IOptions<EndpointOptions> endpointOptions,
            ILocalSiloDetails localSiloDetails,
            ConnectionManager connectionManager)
            : base(serviceProvider, listenerFactory, connectionOptions, connectionManager, trace)
        {
            this.siloConnectionOptions = siloConnectionOptions.Value;
            this.messageCenter = messageCenter;
            this.messageFactory = messageFactory;
            this.trace = trace;
            this.localSiloDetails = localSiloDetails;
            this.connectionManager = connectionManager;
            this.endpointOptions = endpointOptions.Value;
        }

        public override EndPoint Endpoint => this.endpointOptions.GetListeningSiloEndpoint();

        protected override Connection CreateConnection(ConnectionContext context)
        {
            return new SiloConnection(
                default(SiloAddress),
                context,
                this.ConnectionDelegate,
                this.ServiceProvider,
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
            configureDelegate.ConfigureSiloInboundBuilder(connectionBuilder);
            base.ConfigureConnectionBuilder(connectionBuilder);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            if (this.Endpoint is null) return;

            lifecycle.Subscribe(nameof(SiloConnectionListener), ServiceLifecycleStage.RuntimeInitialize, this.OnRuntimeInitializeStart, this.OnRuntimeInitializeStop);
        }

        private async Task OnRuntimeInitializeStart(CancellationToken cancellationToken)
        {
            await Task.Run(() => this.BindAsync(cancellationToken));

            // Start accepting connections immediately.
            await Task.Run(() => this.Start());
        }

        private async Task OnRuntimeInitializeStop(CancellationToken cancellationToken)
        {
            await Task.Run(() => this.StopAsync(cancellationToken));
        }
    }
}

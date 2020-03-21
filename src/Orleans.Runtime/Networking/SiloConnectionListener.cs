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
        internal static readonly object ServicesKey = new object();
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly SiloConnectionOptions siloConnectionOptions;
        private readonly MessageCenter messageCenter;
        private readonly EndpointOptions endpointOptions;
        private readonly ConnectionManager connectionManager;
        private readonly ConnectionCommon connectionShared;

        public SiloConnectionListener(
            IServiceProvider serviceProvider,
            IOptions<ConnectionOptions> connectionOptions,
            IOptions<SiloConnectionOptions> siloConnectionOptions,
            MessageCenter messageCenter,
            IOptions<EndpointOptions> endpointOptions,
            ILocalSiloDetails localSiloDetails,
            ConnectionManager connectionManager,
            ConnectionCommon connectionShared)
            : base(serviceProvider.GetRequiredServiceByKey<object, IConnectionListenerFactory>(ServicesKey), connectionOptions, connectionManager, connectionShared)
        {
            this.siloConnectionOptions = siloConnectionOptions.Value;
            this.messageCenter = messageCenter;
            this.localSiloDetails = localSiloDetails;
            this.connectionManager = connectionManager;
            this.connectionShared = connectionShared;
            this.endpointOptions = endpointOptions.Value;
        }

        public override EndPoint Endpoint => this.endpointOptions.GetListeningSiloEndpoint();

        protected override Connection CreateConnection(ConnectionContext context)
        {
            return new SiloConnection(
                default(SiloAddress),
                context,
                this.ConnectionDelegate,
                this.messageCenter,
                this.localSiloDetails,
                this.connectionManager,
                this.ConnectionOptions,
                this.connectionShared);
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

            lifecycle.Subscribe(nameof(SiloConnectionListener), ServiceLifecycleStage.RuntimeInitialize-1, this.OnRuntimeInitializeStart, this.OnRuntimeInitializeStop);
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

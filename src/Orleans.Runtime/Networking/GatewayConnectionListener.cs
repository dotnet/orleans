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
    internal sealed class GatewayConnectionListener : ConnectionListener, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly INetworkingTrace trace;
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly IOptions<MultiClusterOptions> multiClusterOptions;
        private readonly MessageCenter messageCenter;
        private readonly EndpointOptions endpointOptions;
        private readonly MessageFactory messageFactory;
        private readonly OverloadDetector overloadDetector;
        private readonly Gateway gateway;

        public GatewayConnectionListener(
            IServiceProvider serviceProvider,
            IOptions<ConnectionOptions> connectionOptions,
            IConnectionListenerFactory listenerFactory,
            MessageFactory messageFactory,
            OverloadDetector overloadDetector,
            Gateway gateway,
            INetworkingTrace trace,
            ILocalSiloDetails localSiloDetails,
            IOptions<MultiClusterOptions> multiClusterOptions,
            IOptions<EndpointOptions> endpointOptions,
            MessageCenter messageCenter)
            : base(serviceProvider, listenerFactory, connectionOptions, trace)
        {
            this.messageFactory = messageFactory;
            this.overloadDetector = overloadDetector;
            this.gateway = gateway;
            this.trace = trace;
            this.localSiloDetails = localSiloDetails;
            this.multiClusterOptions = multiClusterOptions;
            this.messageCenter = messageCenter;
            this.endpointOptions = endpointOptions.Value;
        }

        public override EndPoint Endpoint => this.endpointOptions.GetListeningProxyEndpoint();

        protected override Connection CreateConnection(ConnectionContext context)
        {
            return new GatewayInboundConnection(
                context,
                this.ConnectionDelegate,
                this.ServiceProvider,
                this.gateway,
                this.overloadDetector,
                this.messageFactory,
                this.trace,
                this.localSiloDetails,
                this.multiClusterOptions,
                this.ConnectionOptions,
                this.messageCenter,
                this.localSiloDetails);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            if (this.Endpoint is null) return;

            lifecycle.Subscribe(nameof(GatewayConnectionListener), ServiceLifecycleStage.RuntimeInitialize, this.OnRuntimeInitializeStart, this.OnRuntimeInitializeStop);
            lifecycle.Subscribe(nameof(GatewayConnectionListener), ServiceLifecycleStage.Active, this.OnActive, _ => Task.CompletedTask);
        }

        private async Task OnRuntimeInitializeStart(CancellationToken cancellationToken)
        {
            await Task.Run(() => this.BindAsync(cancellationToken));
        }

        private async Task OnRuntimeInitializeStop(CancellationToken cancellationToken)
        {
            await Task.Run(() => this.StopAsync(cancellationToken));
        }

        private async Task OnActive(CancellationToken cancellationToken)
        {
            // Start accepting connections
            await Task.Run(() => this.Start());
        }
    }
}

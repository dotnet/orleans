using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Networking.Shared;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SiloConnectionListener : ConnectionListener, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly INetworkingTrace trace;
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly MessageCenter messageCenter;
        private readonly MessageFactory messageFactory;
        private readonly EndpointOptions endpointOptions;

        public SiloConnectionListener(
            IServiceProvider serviceProvider,
            IOptions<ConnectionOptions> connectionOptions,
            IConnectionListenerFactory listenerFactory,
            MessageCenter messageCenter,
            MessageFactory messageFactory,
            INetworkingTrace trace,
            IOptions<EndpointOptions> endpointOptions,
            ILocalSiloDetails localSiloDetails,
            ISiloStatusOracle siloStatusOracle)
            : base(serviceProvider, listenerFactory, connectionOptions, trace)
        {
            this.messageCenter = messageCenter;
            this.messageFactory = messageFactory;
            this.trace = trace;
            this.localSiloDetails = localSiloDetails;
            this.siloStatusOracle = siloStatusOracle;
            this.endpointOptions = endpointOptions.Value;
        }

        public override EndPoint Endpoint => this.endpointOptions.GetListeningSiloEndpoint();

        protected override Connection CreateConnection(ConnectionContext context)
        {
            return new SiloConnection(
                context,
                this.ConnectionDelegate,
                this.ServiceProvider,
                this.trace,
                this.messageCenter,
                this.messageFactory,
                this.localSiloDetails,
                this.siloStatusOracle);
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

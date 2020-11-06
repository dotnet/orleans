using Orleans.Messaging;
using Orleans.Runtime;

namespace Orleans.ClientObservers
{
    internal interface IClientGatewayObserver : IGrainObserver
    {
        void StopSendingToGateway(SiloAddress gateway);
    }

    internal sealed class ClientGatewayObserver : ClientObserver, IClientGatewayObserver
    {
        private static readonly IdSpan ScopedId = IdSpan.Create(nameof(ClientGatewayObserver));

        private readonly GatewayManager gatewayManager;

        public ClientGatewayObserver(GatewayManager gatewayManager)
        {
            this.gatewayManager = gatewayManager;
        }

        public void StopSendingToGateway(SiloAddress gateway) => this.gatewayManager.MarkAsUnavailableForSend(gateway);

        internal override ObserverGrainId GetObserverGrainId(ClientGrainId clientId) => ObserverGrainId.Create(clientId, ScopedId);

        internal static IClientGatewayObserver GetObserver(IInternalGrainFactory grainFactory, ClientGrainId clientId)
        {
            var observerId = ObserverGrainId.Create(clientId, ScopedId);
            return grainFactory.GetGrain<IClientGatewayObserver>(observerId.GrainId);
        }
    }
}

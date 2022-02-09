using Orleans.Messaging;
using Orleans.Runtime;

namespace Orleans.ClientObservers
{
    /// <summary>
    /// Handles gateway notifications which are sent to connected clients.
    /// </summary>
    internal interface IClientGatewayObserver : IGrainObserver
    {
        /// <summary>
        /// Signals a client that it should stop sending messages to the specified gateway.
        /// </summary>
        /// <param name="gateway">The gateway</param>
        void StopSendingToGateway(SiloAddress gateway);
    }

    /// <summary>
    /// Handles gateway notification events.
    /// </summary>
    internal sealed class ClientGatewayObserver : ClientObserver, IClientGatewayObserver
    {
        private static readonly IdSpan ScopedId = IdSpan.Create(nameof(ClientGatewayObserver));

        private readonly GatewayManager gatewayManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="ClientGatewayObserver"/> class.
        /// </summary>
        /// <param name="gatewayManager">
        /// The gateway manager.
        /// </param>
        public ClientGatewayObserver(GatewayManager gatewayManager)
        {
            this.gatewayManager = gatewayManager;
        }

        /// <inheritdoc />
        public void StopSendingToGateway(SiloAddress gateway) => this.gatewayManager.MarkAsUnavailableForSend(gateway);

        internal override ObserverGrainId GetObserverGrainId(ClientGrainId clientId) => ObserverGrainId.Create(clientId, ScopedId);

        internal static IClientGatewayObserver GetObserver(IInternalGrainFactory grainFactory, ClientGrainId clientId)
        {
            var observerId = ObserverGrainId.Create(clientId, ScopedId);
            return grainFactory.GetGrain<IClientGatewayObserver>(observerId.GrainId);
        }
    }
}

using Orleans.CodeGeneration;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Orleans.ClientObservers
{
    [TypeCodeOverride(ClientGatewayObserver.InterfaceId)]
    internal interface IClientGatewayObserver : IGrainObserver
    {
        [MethodId(ClientGatewayObserver.MethodId)]
        void StopSendingToGateway(SiloAddress gateway);
    }

    internal class ClientGatewayObserver : ClientObserver, IClientGatewayObserver
    {
        internal const int InterfaceId = 0x6C8D70A6;
        internal const int MethodId = unchecked((int)0xFD72F2DC);
        internal static IdSpan Id => IdSpan.Create(nameof(ClientGatewayObserver));

        public override IdSpan ObserverId => Id;

        private GatewayManager gatewayManager;

        public ClientGatewayObserver(GatewayManager gatewayManager)
        {
            this.gatewayManager = gatewayManager;
        }

        public void StopSendingToGateway(SiloAddress gateway)
        {
            this.gatewayManager.MarkAsUnavailableForSend(gateway);
        }

        internal static Message CreateMessage(SiloAddress gateway)
        {
            return new Message
            {
                Direction = Message.Directions.OneWay,
                SendingSilo = gateway,
                TargetGrain = ObserverGrainId.Create(ClientGrainId.Create("broadcast"), Id).GrainId,
                BodyObject = new InvokeMethodRequest(InterfaceId, MethodId, new object[] { gateway })
            };
        }
    }
}

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
        internal static GuidId Id => GuidId.FromParsableString("00000000-0000-0000-0000-000000000001");

        public override GuidId ObserverId => Id;

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
                TargetObserverId = Id,
                BodyObject = new InvokeMethodRequest(InterfaceId, 0, MethodId, new object[] { gateway })
            };
        }
    }
}

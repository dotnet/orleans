using System;

namespace Orleans.Runtime.Messaging
{
    internal interface ISiloMessageCenter : IMessageCenter
    {
        Action<Message> RerouteHandler { set; }

        Action<Message> SniffIncomingMessage { set; }

        void RerouteMessage(Message message);

        bool IsProxying { get; }

        bool TryDeliverToProxy(Message msg);

        void StopAcceptingClientMessages();

        void BlockApplicationMessages();

        Func<SiloAddress, bool> SiloDeadOracle { get; set; }
    }
}

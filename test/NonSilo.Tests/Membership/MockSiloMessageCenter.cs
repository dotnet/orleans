using System;
using System.Collections.Generic;
using System.Threading;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;

namespace NonSilo.Tests.Membership
{
    internal class MockSiloMessageCenter : ISiloMessageCenter
    {
        public List<SiloAddress> CloseCommunicationWithCalls { get; } = new List<SiloAddress>();

        public void CloseCommunicationWith(SiloAddress silo)
        {
            CloseCommunicationWithCalls.Add(silo);
        }

        #region NotImplemented
        public Action<Message> RerouteHandler { set => throw new NotImplementedException(); }
        public Action<Message> SniffIncomingMessage { set => throw new NotImplementedException(); }

        public bool IsProxying => throw new NotImplementedException();

        public Func<SiloAddress, bool> SiloDeadOracle { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public SiloAddress MyAddress => throw new NotImplementedException();

        public int SendQueueLength => throw new NotImplementedException();

        public int ReceiveQueueLength => throw new NotImplementedException();

        public void BlockApplicationMessages()
        {
            throw new NotImplementedException();
        }

        public void PrepareToStop()
        {
            throw new NotImplementedException();
        }

        public void RegisterLocalMessageHandler(Message.Categories category, Action<Message> handler)
        {
            throw new NotImplementedException();
        }

        public void RerouteMessage(Message message)
        {
            throw new NotImplementedException();
        }

        public void SendMessage(Message msg)
        {
            throw new NotImplementedException();
        }

        public void SetHostedClient(IHostedClient client)
        {
            throw new NotImplementedException();
        }

        public void Start()
        {
            throw new NotImplementedException();
        }

        public void Stop()
        {
            throw new NotImplementedException();
        }

        public void StopAcceptingClientMessages()
        {
            throw new NotImplementedException();
        }

        public bool TryDeliverToProxy(Message msg)
        {
            throw new NotImplementedException();
        }

        public Message WaitMessage(Message.Categories type, CancellationToken ct)
        {
            throw new NotImplementedException();
        }
        #endregion
    }
}

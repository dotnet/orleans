using System.Threading;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    internal interface IMessageCenter
    {
        SiloAddress MyAddress { get; }

        void Start();

        void PrepareToStop();

        void Stop();

        void SendMessage(Message msg);

        Message WaitMessage(Message.Categories type, CancellationToken ct);

        int SendQueueLength { get; }

        int ReceiveQueueLength { get; }

        IMessagingConfiguration MessagingConfiguration { get; }
    }
}

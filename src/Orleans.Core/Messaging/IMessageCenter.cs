using System;
using System.Threading;

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

        void RegisterLocalMessageHandler(Message.Categories category, Action<Message> handler);

        int SendQueueLength { get; }

        int ReceiveQueueLength { get; }
    }
}

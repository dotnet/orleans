using System;
using System.Threading.Channels;

namespace Orleans.Runtime
{
    internal interface IMessageCenter
    {
        SiloAddress MyAddress { get; }

        void Start();
        
        void Stop();

        void SendMessage(Message msg);

        void OnReceivedMessage(Message message);

        int SendQueueLength { get; }
    }
}

using System;
using System.Threading.Channels;

namespace Orleans.Runtime
{
    internal interface IMessageCenter
    {
        SiloAddress MyAddress { get; }

        void Start();
        
        void Stop();

        ChannelReader<Message> GetReader(Message.Categories type);

        void SendMessage(Message msg);

        void OnReceivedMessage(Message message);

        void RegisterLocalMessageHandler(Message.Categories category, Action<Message> handler);

        int SendQueueLength { get; }

        int ReceiveQueueLength { get; }
    }
}

using System.Threading;
using System.Threading.Tasks.Dataflow;
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

        void AddTargetBlock(Message.Categories type, ITargetBlock<Message> actionBlock);

        int SendQueueLength { get; }

        int ReceiveQueueLength { get; }

        IMessagingConfiguration MessagingConfiguration { get; }
    }
}

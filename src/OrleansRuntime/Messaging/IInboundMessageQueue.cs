using System;
using System.Threading.Tasks.Dataflow;

namespace Orleans.Runtime.Messaging
{
    internal interface IInboundMessageQueue
    {
        int Count { get; }

        void Stop();

        void PostMessage(Message message);

        void AddTargetBlock(Message.Categories type, Action<Message> actionBlock);
    }
}

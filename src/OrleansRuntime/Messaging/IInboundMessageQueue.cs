using System;

namespace Orleans.Runtime.Messaging
{
    internal interface IInboundMessageQueue
    {
        int Count { get; }

        void Stop();

        void PostMessage(Message message);
        void PostShortCircuitMessage(Message msg);

        void AddTargetBlock(Message.Categories type, Action<Message> actionBlock);
        void AddShortCicruitTargetBlock(Message.Categories type, Action<Message> actionBlock);
    }
}

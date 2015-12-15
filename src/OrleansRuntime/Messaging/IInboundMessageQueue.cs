namespace Orleans.Runtime.Messaging
{
    internal interface IInboundMessageQueue
    {
        int Count { get; }

        void Stop();

        void PostMessage(Message message);

        Message WaitMessage(Message.Categories type);
    }
}

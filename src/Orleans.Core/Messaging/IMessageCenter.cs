namespace Orleans.Runtime
{
    internal interface IMessageCenter
    {
        void SendMessage(Message msg, IMessageReceiverCache? receiverCache);

        void DispatchLocalMessage(Message message);
    }
}

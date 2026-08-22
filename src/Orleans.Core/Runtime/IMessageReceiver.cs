namespace Orleans.Runtime;

internal interface IMessageReceiver
{
    void ReceiveMessage(Message message, IMessageReceiverCache cache);
}

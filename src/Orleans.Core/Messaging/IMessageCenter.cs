namespace Orleans.Runtime
{
    internal interface IMessageCenter
    {
        void SendMessage(Message msg, GrainReference targetReference);

        void DispatchLocalMessage(Message message);
    }
}

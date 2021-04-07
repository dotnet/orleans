namespace Orleans.Runtime
{
    internal interface IMessageCenter
    {
        void Start();
        
        void Stop();

        void SendMessage(Message msg);

        void DispatchLocalMessage(Message message);
    }
}

using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal interface IMessageCenter
    {
        SiloAddress MyAddress { get; }

        Task Start();

        Task Stop();

        void SendMessage(Message msg);

        void OnReceivedMessage(Message message);

        int SendQueueLength { get; }
    }
}

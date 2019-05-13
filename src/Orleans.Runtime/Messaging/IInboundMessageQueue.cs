using System;
using System.Threading;

namespace Orleans.Runtime.Messaging
{
    internal interface IInboundMessageQueue : IDisposable
    {
        int Count { get; }

        void Stop();

        void PostMessage(Message message);

        Message WaitMessage(Message.Categories type, CancellationToken cancellationToken);
    }
}
